using Grand.Business.Core.Interfaces.Common.Configuration;
using Grand.Domain.Permissions;
using Grand.Web.Common.Controllers;
using Grand.Web.Common.Filters;
using Grand.Web.Common.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shipping.CarrierTracking.Models;

namespace Shipping.CarrierTracking.Areas.Admin.Controllers;

[AuthorizeAdmin]
[Area("Admin")]
[PermissionAuthorize(PermissionSystemName.ShippingSettings)]
public class CarrierTrackingController : BaseAdminPluginController
{
    private readonly ISettingService _settingService;

    public CarrierTrackingController(ISettingService settingService)
    {
        _settingService = settingService;
    }

    public async Task<IActionResult> Configure()
    {
        var settings = await _settingService.LoadSetting<CarrierTrackingSettings>();

        return View(new ConfigurationModel {
            SigningSecret = settings.SigningSecret,
            TrackingUrlTemplate = settings.TrackingUrlTemplate,
            MaxAttempts = settings.MaxAttempts,
            //absolute, so it can be pasted straight into the carrier's console
            WebhookUrl = $"{Request.Scheme}://{Request.Host}/{CarrierTrackingDefaults.WebhookRoute}"
        });
    }

    [HttpPost]
    [AutoValidateAntiforgeryToken]
    public async Task<IActionResult> Configure(ConfigurationModel model)
    {
        if (!ModelState.IsValid)
            return await Configure();

        var settings = await _settingService.LoadSetting<CarrierTrackingSettings>();

        settings.SigningSecret = model.SigningSecret;
        settings.TrackingUrlTemplate = model.TrackingUrlTemplate;
        //a zero or negative ceiling would retry a poison payload forever
        settings.MaxAttempts = model.MaxAttempts < 1 ? 1 : model.MaxAttempts;

        await _settingService.SaveSetting(settings);
        //settings are cached per type; without this the running instance keeps the old secret
        await _settingService.ClearCache();

        Success("Settings saved");

        return await Configure();
    }
}
