using FluentValidation;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Infrastructure.Validators;
using Grand.Web.AdminShared.Models.Settings;
using System.Text.Json;

namespace Grand.Web.AdminShared.Validators.Settings;

public class FrontendAssetSettingsValidator : BaseGrandValidator<FrontendAssetSettingsModel>
{
    public FrontendAssetSettingsValidator(
        IEnumerable<IValidatorConsumer<FrontendAssetSettingsModel>> validators,
        ITranslationService translationService)
        : base(validators)
    {
        //Both of these fail silently in the browser rather than in the application, so they are
        //worth catching on the way in: a bad base URL 404s every bundle, and a malformed manifest
        //falls back to the local files without saying so.
        RuleFor(x => x.BaseUrl)
            .Must(BeAnAbsoluteUrlWithTrailingSlash)
            .When(x => !string.IsNullOrWhiteSpace(x.BaseUrl))
            .WithMessage(translationService.GetResource("Admin.Settings.FrontendAsset.BaseUrl.Invalid"));

        RuleFor(x => x.Manifest)
            .Must(BeValidManifestJson)
            .When(x => !string.IsNullOrWhiteSpace(x.Manifest))
            .WithMessage(translationService.GetResource("Admin.Settings.FrontendAsset.Manifest.Invalid"));
    }

    private static bool BeAnAbsoluteUrlWithTrailingSlash(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && url.EndsWith('/');

    private static bool BeValidManifestJson(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            //an "assets" object is what the resolver reads; without it the manifest parses but
            //resolves nothing, which looks identical to not having configured it at all
            return doc.RootElement.TryGetProperty("assets", out var assets)
                   && assets.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
