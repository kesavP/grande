using Grand.Business.Core.Interfaces.Common.Configuration;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Infrastructure.Plugins;

namespace Shipping.CarrierTracking;

public class CarrierTrackingPlugin(
    IPluginTranslateResource pluginTranslateResource,
    ISettingService settingService)
    : BasePlugin, IPlugin
{
    public override string ConfigurationUrl()
    {
        return CarrierTrackingDefaults.ConfigurationUrl;
    }

    public override async Task Install()
    {
        //No signing secret by default. The webhook rejects every request until one is configured,
        //which is the correct posture for an anonymous endpoint - a default would be a shared
        //secret that is not secret.
        await settingService.SaveSetting(new CarrierTrackingSettings {
            SigningSecret = "",
            TrackingUrlTemplate = "",
            MaxAttempts = 5
        });

        await pluginTranslateResource.AddOrUpdatePluginTranslateResource(
            "Shipping.CarrierTracking.FriendlyName", "Carrier delivery tracking");
        await pluginTranslateResource.AddOrUpdatePluginTranslateResource(
            "Shipping.CarrierTracking.Fields.SigningSecret", "Signing secret");
        await pluginTranslateResource.AddOrUpdatePluginTranslateResource(
            "Shipping.CarrierTracking.Fields.TrackingUrlTemplate", "Tracking URL template");
        await pluginTranslateResource.AddOrUpdatePluginTranslateResource(
            "Shipping.CarrierTracking.Fields.MaxAttempts", "Max delivery attempts");

        await base.Install();
    }

    public override async Task Uninstall()
    {
        await settingService.DeleteSetting<CarrierTrackingSettings>();

        await pluginTranslateResource.DeletePluginTranslationResource(
            "Shipping.CarrierTracking.FriendlyName");
        await pluginTranslateResource.DeletePluginTranslationResource(
            "Shipping.CarrierTracking.Fields.SigningSecret");
        await pluginTranslateResource.DeletePluginTranslationResource(
            "Shipping.CarrierTracking.Fields.TrackingUrlTemplate");
        await pluginTranslateResource.DeletePluginTranslationResource(
            "Shipping.CarrierTracking.Fields.MaxAttempts");

        await base.Uninstall();
    }
}
