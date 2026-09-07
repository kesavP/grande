# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build-env
LABEL stage=build-env
WORKDIR /app

# Copy 
COPY Directory.Packages.props /app/
COPY ./src/ /app/

ARG GIT_COMMIT
ARG GIT_BRANCH

# Build modules
RUN for module in /app/Modules/*; do \
    dotnet build "$module" -c Release -p:SourceRevisionId=$GIT_COMMIT -p:GitBranch=$GIT_BRANCH; \
  done

# Build plugins
RUN for plugin in /app/Plugins/*; do \
    dotnet build "$plugin" -c Release -p:SourceRevisionId=$GIT_COMMIT -p:GitBranch=$GIT_BRANCH; \
  done

# Publish Web project
# INCLUDE_ADMIN=false leaves the admin panel out, for deployments that run it as its own
# container (target "admin" below). The default keeps the single all-in-one image.
FROM build-env AS publish-web
ARG GIT_COMMIT
ARG GIT_BRANCH
ARG INCLUDE_ADMIN=true
RUN dotnet publish /app/Web/Grand.Web/Grand.Web.csproj -c Release -o ./build/release -p:SourceRevisionId=$GIT_COMMIT -p:GitBranch=$GIT_BRANCH -p:IncludeAdminPanel=$INCLUDE_ADMIN

# Publish the admin panel as its own application
FROM build-env AS publish-admin
ARG GIT_COMMIT
ARG GIT_BRANCH
RUN dotnet publish /app/Web/Grand.Web.Admin/Grand.Web.Admin.csproj -c Release -o ./build/admin -p:SourceRevisionId=$GIT_COMMIT -p:GitBranch=$GIT_BRANCH -p:StandaloneAdmin=true

# Admin runtime - build with: docker build --target admin
#
# It is a second host over the SAME database, not a separate service, so it needs the same
# ConnectionStrings__Mongodb and the same Security__CookiePrefix, Security__CookieClaimsIssuer,
# Security__PasswordHashKey and Customer__RegisterCustomersPerStore as the storefront.
# Three things are per-container state and have to be dealt with when it is split off:
#  - App_Data/InstalledPlugins.cfg is written locally by the plugin installer, so set
#    Extensions__InstalledPlugins (a comma-separated list, which overrides the file) to the
#    same value on both containers, or they disagree about which plugins are installed.
#  - IMediaFileStore and the elFinder file manager always write to the local wwwroot
#    (wwwroot/assets/images/uploaded and /thumbs) even when pictures go to Azure/Amazon blob
#    storage, so mount shared storage there if uploads must be visible to the storefront.
#  - migrations have no cross-process lock, so only one host may run them. Nothing to set: the
#    Admin's own App_Data/appsettings.json already ships Grand.Module.Migration (and Installer,
#    and ScheduledTasks) as false, so the storefront owns them. Do not enable them here.
# Scheduled tasks need no flag either - only Grand.Web's Program.cs calls RegisterTasks.
# Storefront themes are not in this image - see the comment on PublishPluginsAndModules in
# Grand.Web.Admin.csproj - so the theme picker in Settings -> General/Common is empty here.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS admin

EXPOSE 8080
WORKDIR /app
COPY --from=publish-admin /app/build/admin .

RUN chown -R app:app /app/App_Data /app/wwwroot /app/Plugins

USER app

ENTRYPOINT ["dotnet", "Grand.Web.Admin.dll"]

# Runtime stage - the default target, so plain "docker build ." is unchanged
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

EXPOSE 8080
WORKDIR /app
COPY --from=publish-web /app/build/release .

RUN chown -R app:app /app/App_Data /app/wwwroot /app/Plugins

USER app

ENTRYPOINT ["dotnet", "Grand.Web.dll"]
