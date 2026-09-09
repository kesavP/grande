using Grand.Domain.Configuration;

namespace Grand.Domain.Media;

/// <summary>
///     Where the storefront's JavaScript and CSS bundles are served from, and the integrity
///     hashes the browser checks them against.
///
///     Empty BaseUrl (the default) serves the bundles shipped inside the image from /bundles,
///     exactly as before. Setting it moves them to a CDN and decouples a frontend release from
///     an application rebuild: upload the files, update these settings, done.
///
///     These values live in the database on purpose. Subresource Integrity is only meaningful
///     when the hash reaches the browser by a path the file's own host cannot write - publish
///     both from the same storage account and an attacker changes the file and its hash
///     together. Storing hashes here, written through the application, means a compromised
///     storage account can replace a bundle but cannot make a browser execute it.
/// </summary>
public class FrontendAssetSettings : ISettings
{
    /// <summary>
    ///     Absolute base URL, with trailing slash, e.g. https://cdn.example.com/bundles/v3/.
    ///     Include a version segment: changing it is what makes a release atomic and a rollback
    ///     a one-line change. Empty serves from the local /bundles folder.
    /// </summary>
    public string BaseUrl { get; set; }

    /// <summary>
    ///     Contents of wwwroot/bundles/asset-manifest.json, produced by the frontend build.
    ///     Maps a logical asset name to its file and integrity hash.
    /// </summary>
    public string Manifest { get; set; }

    /// <summary>
    ///     Emit integrity/crossorigin attributes. Turn off only to diagnose a mismatch - a
    ///     blocked script is silent in the page and visible only in the browser console.
    /// </summary>
    public bool UseSubresourceIntegrity { get; set; } = true;
}
