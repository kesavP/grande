using Grand.Infrastructure.ModelBinding;
using Grand.Infrastructure.Models;

namespace Shipping.CarrierTracking.Models;

public class ConfigurationModel : BaseModel
{
    [GrandResourceDisplayName("Shipping.CarrierTracking.Fields.SigningSecret")]
    public string SigningSecret { get; set; }

    [GrandResourceDisplayName("Shipping.CarrierTracking.Fields.TrackingUrlTemplate")]
    public string TrackingUrlTemplate { get; set; }

    [GrandResourceDisplayName("Shipping.CarrierTracking.Fields.MaxAttempts")]
    public int MaxAttempts { get; set; }

    /// <summary>
    ///     Shown read-only so an operator can copy the exact URL to register with the carrier.
    /// </summary>
    public string WebhookUrl { get; set; }
}
