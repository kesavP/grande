using Grand.Business.Core.Interfaces.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Grand.Web.Common.TagHelpers;

[HtmlTargetElement("script", Attributes = LocationAttributeName)]
[HtmlTargetElement("script", Attributes = SrcAttributeName)]
[HtmlTargetElement("script", Attributes = OrderAttributeName)]
public class ScriptTagHelper : TagHelper
{
    private const string LocationAttributeName = "asp-location";
    private const string SrcAttributeName = "asp-src";
    private const string OrderAttributeName = "asp-order";
    private const string AppendVersionAttributeName = "asp-append-version";
    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly IResourceManager _resourceManager;
    private readonly IFrontendAssetResolver _assetResolver;

    public ScriptTagHelper(IResourceManager resourceManager, IHttpContextAccessor httpContextAccessor,
        IFileVersionProvider fileVersionProvider, IFrontendAssetResolver assetResolver)
    {
        _resourceManager = resourceManager;
        _httpContextAccessor = httpContextAccessor;
        _fileVersionProvider = fileVersionProvider;
        _assetResolver = assetResolver;
    }

    [HtmlAttributeName(LocationAttributeName)]
    public ScriptLocation Location { get; set; }

    [HtmlAttributeName(SrcAttributeName)] public string Src { get; set; }

    [HtmlAttributeName(OrderAttributeName)]
    public int DisplayOrder { get; set; }

    /// <summary>
    ///     Appends a content hash to the src so a changed script actually reaches
    ///     returning visitors. Without it a cached copy can go on running against
    ///     markup that has already moved on.
    /// </summary>
    /// <remarks>
    ///     Nullable to match the built-in ScriptTagHelper, which also binds this
    ///     attribute on &lt;script&gt; - a plain bool makes Razor fail to compile.
    /// </remarks>
    [HtmlAttributeName(AppendVersionAttributeName)]
    public bool? AppendVersion { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var isAjaxCall = _httpContextAccessor.HttpContext != null &&
                         _httpContextAccessor.HttpContext.Request.Headers["x-requested-with"] == "XMLHttpRequest";
        if (!isAjaxCall)
        {
            output.SuppressOutput();

            var childContent = await output.GetChildContentAsync();

            var builder = new TagBuilder("script");
            builder.InnerHtml.AppendHtml(childContent);
            builder.TagRenderMode = TagRenderMode.Normal;
            if (!string.IsNullOrEmpty(Src))
            {
                var src = Src;

                //A bundle published to a CDN is addressed by its manifest entry, which also
                //carries the integrity hash. Resolve returns null for anything not in the
                //manifest, so every other script keeps the path written in the view.
                var asset = ResolveAsset(Src);
                if (asset != null)
                {
                    src = asset.Url;
                    if (!string.IsNullOrEmpty(asset.Integrity))
                    {
                        builder.Attributes["integrity"] = asset.Integrity;
                        //required for SRI on a cross-origin script; harmless same-origin
                        builder.Attributes["crossorigin"] = "anonymous";
                    }
                }
                //asp-append-version is redundant once a manifest is in play - the URL already
                //changes with the release - and it would hash a file no longer served locally
                else if (AppendVersion == true && _httpContextAccessor.HttpContext != null)
                {
                    src = _fileVersionProvider.AddFileVersionToPath(
                        _httpContextAccessor.HttpContext.Request.PathBase, src);
                }

                builder.Attributes.Add("src", src);
            }
            foreach (var attribute in output.Attributes)
                builder.Attributes.Add(attribute.Name, attribute.Value.ToString());

            switch (Location)
            {
                case ScriptLocation.Head:
                    _resourceManager.RegisterHeadScript(builder, DisplayOrder);
                    break;

                case ScriptLocation.Header:
                    _resourceManager.RegisterHeaderScript(builder, DisplayOrder);
                    break;

                case ScriptLocation.Footer:
                    _resourceManager.RegisterFootScript(builder, DisplayOrder);
                    break;
            }
        }
    }

    /// <summary>
    ///     Looks up "/bundles/x.js" by file name. Null when the path is not a managed bundle or
    ///     no manifest is configured - which is what keeps the default in-image deployment
    ///     working unchanged.
    /// </summary>
    private FrontendAsset ResolveAsset(string src)
    {
        const string prefix = "/bundles/";
        if (string.IsNullOrEmpty(src) || !src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return _assetResolver.Resolve(src[prefix.Length..]);
    }
}