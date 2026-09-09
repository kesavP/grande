using Grand.Infrastructure.ModelBinding;
using Grand.Infrastructure.Models;

namespace Grand.Web.AdminShared.Models.Settings;

/// <summary>
///     Has no ActiveStore, unlike the other settings models: this screen writes to the global
///     scope only, because the singleton that reads it cannot be aimed at a particular store.
///     See the remarks on SettingController.FrontendAsset.
/// </summary>
public class FrontendAssetSettingsModel : BaseModel
{
    [GrandResourceDisplayName("Admin.Settings.FrontendAsset.BaseUrl")]
    public string BaseUrl { get; set; }

    [GrandResourceDisplayName("Admin.Settings.FrontendAsset.Manifest")]
    public string Manifest { get; set; }

    [GrandResourceDisplayName("Admin.Settings.FrontendAsset.UseSubresourceIntegrity")]
    public bool UseSubresourceIntegrity { get; set; }

    /// <summary>
    ///     Read-only summary of what the current manifest resolves to, so an administrator can
    ///     confirm a release landed without reading the page source.
    /// </summary>
    public IList<ResolvedAssetModel> ResolvedAssets { get; set; } = new List<ResolvedAssetModel>();

    public class ResolvedAssetModel : BaseModel
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Integrity { get; set; }
    }
}
