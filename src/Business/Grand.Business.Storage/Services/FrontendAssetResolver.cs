using Grand.Business.Core.Interfaces.Storage;
using Grand.Domain.Media;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Grand.Business.Storage.Services;

/// <summary>
///     Resolves bundle URLs and integrity hashes from FrontendAssetSettings.
///
///     Registered as a singleton and parses the manifest once: it changes only when an
///     administrator publishes a frontend release, and re-parsing JSON on every page render to
///     emit four script tags would be pure waste.
/// </summary>
public class FrontendAssetResolver : IFrontendAssetResolver
{
    private const string LocalBase = "/bundles/";

    private readonly IReadOnlyDictionary<string, FrontendAsset> _assets;

    public FrontendAssetResolver(FrontendAssetSettings settings, ILogger<FrontendAssetResolver> logger)
    {
        //trailing slash is the caller's to get right, but a missing one silently produces
        //"…/v3app.runtime.bundle.js", so normalise rather than serve a 404
        var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? LocalBase
            : settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/";

        _assets = Parse(settings, baseUrl, logger);
    }

    public FrontendAsset Resolve(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return null;
        return _assets.TryGetValue(assetName, out var asset) ? asset : null;
    }

    private static IReadOnlyDictionary<string, FrontendAsset> Parse(
        FrontendAssetSettings settings, string baseUrl, ILogger logger)
    {
        var empty = new Dictionary<string, FrontendAsset>();

        if (string.IsNullOrWhiteSpace(settings.Manifest)) return empty;

        try
        {
            using var doc = JsonDocument.Parse(settings.Manifest);
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return empty;

            var result = new Dictionary<string, FrontendAsset>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in assets.EnumerateObject())
            {
                var file = entry.Value.TryGetProperty("file", out var f) ? f.GetString() : entry.Name;
                var integrity = settings.UseSubresourceIntegrity &&
                                entry.Value.TryGetProperty("integrity", out var i)
                    ? i.GetString()
                    : null;

                result[entry.Name] = new FrontendAsset(baseUrl + file, integrity);
            }

            return result;
        }
        catch (JsonException ex)
        {
            //A malformed manifest must not take the storefront down. Returning nothing makes
            //every caller fall back to the path written in the view, so the page still renders
            //from the bundles inside the image.
            logger.LogError(ex, "FrontendAssetSettings.Manifest could not be parsed - falling back to local bundles");
            return empty;
        }
    }
}
