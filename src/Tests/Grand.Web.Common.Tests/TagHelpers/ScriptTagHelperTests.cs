using Grand.Business.Core.Interfaces.Storage;
using Grand.Web.Common.TagHelpers;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Encodings.Web;

namespace Grand.Web.Common.Tests.TagHelpers;

/// <summary>
///     The tag helper is what actually puts integrity on the page, so these assert on the
///     rendered markup rather than on the resolver's return value.
/// </summary>
[TestClass]
public class ScriptTagHelperTests
{
    private Mock<IFrontendAssetResolver> _assetResolver;
    private Mock<IFileVersionProvider> _fileVersionProvider;
    private DefaultHttpContext _httpContext;
    private Mock<IHttpContextAccessor> _httpContextAccessor;
    private List<IHtmlContent> _registered;
    private Mock<IResourceManager> _resourceManager;

    [TestInitialize]
    public void Init()
    {
        _registered = [];
        _resourceManager = new Mock<IResourceManager>();
        _resourceManager.Setup(x => x.RegisterHeadScript(It.IsAny<IHtmlContent>(), It.IsAny<int>()))
            .Callback<IHtmlContent, int>((c, _) => _registered.Add(c));
        _resourceManager.Setup(x => x.RegisterFootScript(It.IsAny<IHtmlContent>(), It.IsAny<int>()))
            .Callback<IHtmlContent, int>((c, _) => _registered.Add(c));

        _httpContext = new DefaultHttpContext();
        _httpContextAccessor = new Mock<IHttpContextAccessor>();
        _httpContextAccessor.Setup(x => x.HttpContext).Returns(_httpContext);

        _fileVersionProvider = new Mock<IFileVersionProvider>();
        _fileVersionProvider.Setup(x => x.AddFileVersionToPath(It.IsAny<PathString>(), It.IsAny<string>()))
            .Returns<PathString, string>((_, path) => path + "?v=LOCALHASH");

        _assetResolver = new Mock<IFrontendAssetResolver>();
    }

    private async Task<string> Render(string src, bool? appendVersion = null)
    {
        var helper = new ScriptTagHelper(_resourceManager.Object, _httpContextAccessor.Object,
            _fileVersionProvider.Object, _assetResolver.Object) {
            Src = src,
            Location = ScriptLocation.Footer,
            AppendVersion = appendVersion
        };

        var context = new TagHelperContext([], new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput("script", [],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        await helper.ProcessAsync(context, output);

        Assert.AreEqual(1, _registered.Count, "the helper should have registered exactly one script");

        await using var writer = new StringWriter();
        _registered[0].WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    [TestMethod]
    public async Task ManagedBundle_WithIntegrity_EmitsIntegrityAndCrossorigin()
    {
        _assetResolver.Setup(x => x.Resolve("app.runtime.bundle.js"))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.runtime.bundle.js", "sha384-abc"));

        var html = await Render("/bundles/app.runtime.bundle.js");

        StringAssert.Contains(html, "src=\"https://cdn.example.com/bundles/app.runtime.bundle.js\"");
        StringAssert.Contains(html, "integrity=\"sha384-abc\"");
        StringAssert.Contains(html, "crossorigin=\"anonymous\"");
    }

    [TestMethod]
    public async Task ManagedBundle_WithoutIntegrity_EmitsTheCdnUrlOnly()
    {
        _assetResolver.Setup(x => x.Resolve("app.runtime.bundle.js"))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.runtime.bundle.js", null));

        var html = await Render("/bundles/app.runtime.bundle.js");

        StringAssert.Contains(html, "src=\"https://cdn.example.com/bundles/app.runtime.bundle.js\"");
        Assert.IsFalse(html.Contains("integrity"), "no hash means no integrity attribute");
        Assert.IsFalse(html.Contains("crossorigin"));
    }

    [TestMethod]
    public async Task ManagedBundle_SuppressesAppendVersion()
    {
        _assetResolver.Setup(x => x.Resolve("app.runtime.bundle.js"))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.runtime.bundle.js", "sha384-abc"));

        var html = await Render("/bundles/app.runtime.bundle.js", true);

        //the manifest URL already changes per release; a local file hash would be wrong here
        Assert.IsFalse(html.Contains("?v=LOCALHASH"));
        _fileVersionProvider.Verify(
            x => x.AddFileVersionToPath(It.IsAny<PathString>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task UnmanagedBundle_KeepsTheViewPathAndStillAppendsVersion()
    {
        _assetResolver.Setup(x => x.Resolve(It.IsAny<string>())).Returns((FrontendAsset)null);

        var html = await Render("/bundles/app.runtime.bundle.js", true);

        StringAssert.Contains(html, "src=\"/bundles/app.runtime.bundle.js?v=LOCALHASH\"");
        Assert.IsFalse(html.Contains("integrity"));
    }

    [TestMethod]
    public async Task NonBundlePath_IsNeverLookedUpInTheManifest()
    {
        var html = await Render("/scripts/site.js");

        StringAssert.Contains(html, "src=\"/scripts/site.js\"");
        _assetResolver.Verify(x => x.Resolve(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task AjaxRequest_RendersNothing()
    {
        _httpContext.Request.Headers["x-requested-with"] = "XMLHttpRequest";
        _assetResolver.Setup(x => x.Resolve(It.IsAny<string>()))
            .Returns(new FrontendAsset("https://cdn.example.com/bundles/app.runtime.bundle.js", "sha384-abc"));

        var helper = new ScriptTagHelper(_resourceManager.Object, _httpContextAccessor.Object,
            _fileVersionProvider.Object, _assetResolver.Object) {
            Src = "/bundles/app.runtime.bundle.js", Location = ScriptLocation.Footer
        };
        var output = new TagHelperOutput("script", [],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        await helper.ProcessAsync(
            new TagHelperContext([], new Dictionary<object, object>(), "id"), output);

        Assert.AreEqual(0, _registered.Count);
    }
}
