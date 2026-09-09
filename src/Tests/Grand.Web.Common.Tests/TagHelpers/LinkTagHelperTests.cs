using Grand.Business.Core.Interfaces.Storage;
using Grand.Web.Common.TagHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Encodings.Web;

namespace Grand.Web.Common.Tests.TagHelpers;

/// <summary>
///     Stylesheets go through their own tag helper, so integrity on &lt;link&gt; is a separate
///     code path from &lt;script&gt; and gets its own coverage.
/// </summary>
[TestClass]
public class LinkTagHelperTests
{
    private Mock<IFrontendAssetResolver> _assetResolver;
    private Mock<IFileVersionProvider> _fileVersionProvider;
    private Mock<IHttpContextAccessor> _httpContextAccessor;
    private List<LinkEntry> _registered;
    private Mock<IResourceManager> _resourceManager;

    [TestInitialize]
    public void Init()
    {
        _registered = [];
        _resourceManager = new Mock<IResourceManager>();
        _resourceManager.Setup(x => x.RegisterLink(It.IsAny<LinkEntry>()))
            .Callback<LinkEntry>(e => _registered.Add(e));

        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        _httpContextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext());

        _fileVersionProvider = new Mock<IFileVersionProvider>();
        _fileVersionProvider.Setup(x => x.AddFileVersionToPath(It.IsAny<PathString>(), It.IsAny<string>()))
            .Returns<PathString, string>((_, path) => path + "?v=LOCALHASH");

        _assetResolver = new Mock<IFrontendAssetResolver>();
    }

    private string Render(string src, bool? appendVersion = null)
    {
        var helper = new LinkTagHelper(_resourceManager.Object, _httpContextAccessor.Object,
            _fileVersionProvider.Object, _assetResolver.Object) {
            Src = src,
            Rel = "stylesheet",
            AppendVersion = appendVersion
        };

        var output = new TagHelperOutput("link", [],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        helper.Process(new TagHelperContext([], new Dictionary<object, object>(), "id"), output);

        Assert.AreEqual(1, _registered.Count);

        using var writer = new StringWriter();
        _registered[0].GetTag().WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    [TestMethod]
    public void ManagedBundle_WithIntegrity_EmitsIntegrityAndCrossorigin()
    {
        _assetResolver.Setup(x => x.Resolve("app.bundle.css"))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.bundle.css", "sha384-def"));

        var html = Render("/bundles/app.bundle.css");

        StringAssert.Contains(html, "href=\"https://cdn.example.com/bundles/app.bundle.css\"");
        StringAssert.Contains(html, "integrity=\"sha384-def\"");
        StringAssert.Contains(html, "crossorigin=\"anonymous\"");
    }

    [TestMethod]
    public void ManagedBundle_SuppressesAppendVersion()
    {
        _assetResolver.Setup(x => x.Resolve("app.bundle.css"))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.bundle.css", "sha384-def"));

        var html = Render("/bundles/app.bundle.css", true);

        Assert.IsFalse(html.Contains("?v=LOCALHASH"));
    }

    [TestMethod]
    public void UnmanagedBundle_KeepsTheViewPathAndStillAppendsVersion()
    {
        _assetResolver.Setup(x => x.Resolve(It.IsAny<string>())).Returns((FrontendAsset)null);

        var html = Render("/bundles/app.bundle.css", true);

        StringAssert.Contains(html, "href=\"/bundles/app.bundle.css?v=LOCALHASH\"");
        Assert.IsFalse(html.Contains("integrity"));
    }

    [TestMethod]
    public void NonBundlePath_IsNeverLookedUpInTheManifest()
    {
        var html = Render("/theme/styles.css");

        StringAssert.Contains(html, "href=\"/theme/styles.css\"");
        _assetResolver.Verify(x => x.Resolve(It.IsAny<string>()), Times.Never);
    }
}
