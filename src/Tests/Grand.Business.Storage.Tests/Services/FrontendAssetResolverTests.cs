using Grand.Business.Storage.Services;
using Grand.Domain.Media;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Grand.Business.Storage.Tests.Services;

[TestClass]
public class FrontendAssetResolverTests
{
    private const string Manifest = """
        {
          "assets": {
            "app.runtime.bundle.js": {
              "file": "app.runtime.bundle.js",
              "integrity": "sha384-abc"
            },
            "app.bundle.css": {
              "file": "app.bundle.css",
              "integrity": "sha384-def"
            }
          }
        }
        """;

    private Mock<ILogger<FrontendAssetResolver>> _logger;

    [TestInitialize]
    public void Init()
    {
        _logger = new Mock<ILogger<FrontendAssetResolver>>();
    }

    private FrontendAssetResolver Build(FrontendAssetSettings settings)
    {
        return new FrontendAssetResolver(settings, _logger.Object);
    }

    [TestMethod]
    public void Resolve_WithManifestAndSri_ReturnsCdnUrlAndIntegrity()
    {
        var resolver = Build(new FrontendAssetSettings {
            BaseUrl = "https://cdn.example.com/bundles/",
            Manifest = Manifest,
            UseSubresourceIntegrity = true
        });

        var asset = resolver.Resolve("app.runtime.bundle.js");

        Assert.IsNotNull(asset);
        Assert.AreEqual("https://cdn.example.com/bundles/app.runtime.bundle.js", asset.Url);
        Assert.AreEqual("sha384-abc", asset.Integrity);
    }

    [TestMethod]
    public void Resolve_MissingTrailingSlashOnBaseUrl_StillProducesAValidUrl()
    {
        var resolver = Build(new FrontendAssetSettings {
            BaseUrl = "https://cdn.example.com/bundles",
            Manifest = Manifest,
            UseSubresourceIntegrity = true
        });

        Assert.AreEqual("https://cdn.example.com/bundles/app.bundle.css",
            resolver.Resolve("app.bundle.css").Url);
    }

    [TestMethod]
    public void Resolve_SriDisabled_KeepsUrlButDropsIntegrity()
    {
        var resolver = Build(new FrontendAssetSettings {
            BaseUrl = "https://cdn.example.com/bundles/",
            Manifest = Manifest,
            UseSubresourceIntegrity = false
        });

        var asset = resolver.Resolve("app.runtime.bundle.js");

        Assert.AreEqual("https://cdn.example.com/bundles/app.runtime.bundle.js", asset.Url);
        Assert.IsNull(asset.Integrity);
    }

    [TestMethod]
    public void Resolve_NoBaseUrl_FallsBackToTheLocalBundlesPath()
    {
        var resolver = Build(new FrontendAssetSettings {
            Manifest = Manifest,
            UseSubresourceIntegrity = true
        });

        Assert.AreEqual("/bundles/app.runtime.bundle.js", resolver.Resolve("app.runtime.bundle.js").Url);
    }

    [TestMethod]
    public void Resolve_NoManifest_ReturnsNullSoTheViewPathIsUsed()
    {
        var resolver = Build(new FrontendAssetSettings { BaseUrl = "https://cdn.example.com/bundles/" });

        Assert.IsNull(resolver.Resolve("app.runtime.bundle.js"));
    }

    [TestMethod]
    public void Resolve_MalformedManifest_ReturnsNullRatherThanThrowing()
    {
        var resolver = Build(new FrontendAssetSettings {
            BaseUrl = "https://cdn.example.com/bundles/",
            Manifest = "{ not json",
            UseSubresourceIntegrity = true
        });

        Assert.IsNull(resolver.Resolve("app.runtime.bundle.js"));
    }

    [TestMethod]
    public void Resolve_UnknownAssetName_ReturnsNull()
    {
        var resolver = Build(new FrontendAssetSettings { Manifest = Manifest, UseSubresourceIntegrity = true });

        Assert.IsNull(resolver.Resolve("does-not-exist.js"));
        Assert.IsNull(resolver.Resolve(""));
        Assert.IsNull(resolver.Resolve(null));
    }
}
