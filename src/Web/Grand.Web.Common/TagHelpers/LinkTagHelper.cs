using Grand.Business.Core.Interfaces.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Grand.Web.Common.TagHelpers;

[HtmlTargetElement("link", TagStructure = TagStructure.WithoutEndTag, Attributes = SrcAttributeName)]
[HtmlTargetElement("link", TagStructure = TagStructure.WithoutEndTag, Attributes = SrcPriority)]
public class LinkTagHelper : TagHelper
{
    private const string SrcAttributeName = "asp-src";
    private const string SrcPriority = "asp-priority";
    private const string AppendVersionAttributeName = "asp-append-version";

    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly IResourceManager _resourceManager;

    private readonly IFrontendAssetResolver _assetResolver;

    public LinkTagHelper(IResourceManager resourceManager, IHttpContextAccessor httpContextAccessor,
        IFileVersionProvider fileVersionProvider, IFrontendAssetResolver assetResolver)
    {
        _resourceManager = resourceManager;
        _httpContextAccessor = httpContextAccessor;
        _fileVersionProvider = fileVersionProvider;
        _assetResolver = assetResolver;
    }

    [HtmlAttributeName(SrcPriority)] public int Priority { get; set; }

    /// <summary>
    ///     Appends a content hash to the href so a changed stylesheet actually reaches
    ///     returning visitors.
    /// </summary>
    /// <remarks>
    ///     Views passed asp-append-version on &lt;link&gt; long before this property existed.
    ///     The attribute was not silently ignored - the built-in LinkTagHelper binds it too,
    ///     but only stamps a plain href, and these links carry asp-src instead, so nothing
    ///     was ever versioned. Nullable for the same reason as in ScriptTagHelper: the
    ///     built-in helper binds the same attribute and a plain bool breaks Razor.
    ///     Paths the web root file provider cannot resolve (plugin content is served from
    ///     its own provider) are returned unchanged rather than failing.
    /// </remarks>
    [HtmlAttributeName(AppendVersionAttributeName)]
    public bool? AppendVersion { get; set; }

    public string Rel { get; set; }

    [HtmlAttributeName(SrcAttributeName)] public string Src { get; set; }

    public string Title { get; set; }

    public string Type { get; set; }

    public string Condition { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var linkEntry = new LinkEntry {
            Priority = Priority
        };

        if (!string.IsNullOrEmpty(Src))
        {
            var href = Src;

            //a stylesheet published to a CDN comes from the manifest, with its integrity hash
            var asset = ResolveAsset(Src);
            if (asset != null)
            {
                href = asset.Url;
                if (!string.IsNullOrEmpty(asset.Integrity))
                {
                    linkEntry.SetAttribute("integrity", asset.Integrity);
                    //required for SRI on a cross-origin stylesheet; harmless same-origin
                    linkEntry.SetAttribute("crossorigin", "anonymous");
                }
            }
            //redundant once a manifest is in play, and it would hash a file no longer local
            else if (AppendVersion == true && _httpContextAccessor.HttpContext != null)
            {
                href = _fileVersionProvider.AddFileVersionToPath(
                    _httpContextAccessor.HttpContext.Request.PathBase, href);
            }

            linkEntry.Href = href;
        }

        if (!string.IsNullOrEmpty(Rel)) linkEntry.Rel = Rel;

        if (!string.IsNullOrEmpty(Condition)) linkEntry.Condition = Condition;

        if (!string.IsNullOrEmpty(Title)) linkEntry.Title = Title;

        if (!string.IsNullOrEmpty(Type)) linkEntry.Type = Type;

        foreach (var attribute in output.Attributes)
        {
            if (string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase)) continue;

            linkEntry.SetAttribute(attribute.Name, attribute.Value.ToString());
        }

        _resourceManager.RegisterLink(linkEntry);

        output.TagName = null;
    }

    /// <summary>
    ///     Looks up "/bundles/x.css" by file name; null when it is not a managed bundle.
    /// </summary>
    private FrontendAsset ResolveAsset(string src)
    {
        const string prefix = "/bundles/";
        if (string.IsNullOrEmpty(src) || !src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return _assetResolver.Resolve(src[prefix.Length..]);
    }
}