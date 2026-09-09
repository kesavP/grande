namespace Grand.Business.Core.Interfaces.Storage;

/// <summary>
///     Resolved location of a frontend bundle: where to fetch it and, when known, the hash the
///     browser should check it against.
/// </summary>
public record FrontendAsset(string Url, string Integrity);

/// <summary>
///     Resolves a logical bundle name - "app.runtime.bundle.js" - to the URL a page should
///     request and its integrity hash.
/// </summary>
public interface IFrontendAssetResolver
{
    /// <summary>
    ///     Returns null when the name is not in the manifest, so a caller can fall back to the
    ///     path written in the view rather than emitting a broken tag.
    /// </summary>
    FrontendAsset Resolve(string assetName);
}
