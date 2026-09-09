using Grand.Data;
using Grand.Domain.Admin;
using Grand.Domain.Permissions;
using Grand.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grand.Module.Migration.Migrations._2._4;

public class MigrationFrontendAssetSiteMap : IMigration
{
    private const string NodeSystemName = "Frontend asset settings";

    public int Priority => 2;
    public DbVersion Version => new(2, 4);
    public Guid Identity => new("3F1B6A84-2C77-4D5E-9A08-6B4E1D0C7F32");
    public string Name => "Add Frontend asset settings to the admin site map";

    /// <summary>
    ///     Adds the Frontend asset settings entry to the admin menu for installations that already
    ///     exist. A fresh installation gets it from StandardAdminSiteMap; without this an upgraded
    ///     store has the screen but no way to reach it except by typing the URL.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    public bool UpgradeProcess(IServiceProvider serviceProvider)
    {
        var repository = serviceProvider.GetRequiredService<IRepository<AdminSiteMap>>();
        var logService = serviceProvider.GetRequiredService<ILogger<MigrationFrontendAssetSiteMap>>();

        try
        {
            var sitemapSettings = repository.Table.FirstOrDefault(x => x.SystemName == "Settings");
            if (sitemapSettings == null) return true;

            //idempotent - a second run must not add the entry twice
            if (sitemapSettings.ChildNodes.Any(x => x.SystemName == NodeSystemName)) return true;

            sitemapSettings.ChildNodes.Add(new AdminSiteMap {
                SystemName = NodeSystemName,
                ResourceName = "Admin.Settings.FrontendAsset",
                PermissionNames = new List<string> { PermissionSystemName.Settings, PermissionSystemName.System },
                ControllerName = "Setting",
                ActionName = "FrontendAsset",
                DisplayOrder = 10,
                IconClass = "fa fa-dot-circle-o"
            });

            repository.Update(sitemapSettings);
        }
        catch (Exception ex)
        {
            //false stops the run before the version stamp, so this is retried on the next start
            //rather than being recorded as done - see MigrationProcess.RunMigrationProcess
            logService.LogError(ex, "UpgradeProcess - FrontendAssetSiteMap");
            return false;
        }

        return true;
    }
}
