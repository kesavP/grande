using Grand.Domain.Configuration;

namespace Shipping.CarrierTracking;

public class CarrierTrackingSettings : ISettings
{
    /// <summary>
    ///     Shared secret the carrier signs its payloads with. The webhook endpoint is anonymous - it
    ///     has to be, the carrier cannot authenticate - so this signature is the only thing
    ///     distinguishing a real delivery event from anyone who guesses the URL.
    /// </summary>
    public string SigningSecret { get; set; }

    /// <summary>
    ///     Template for the customer-facing tracking page, with {0} replaced by the tracking number.
    /// </summary>
    public string TrackingUrlTemplate { get; set; }

    /// <summary>
    ///     Stop retrying an event after this many failed attempts, so one unparseable payload does
    ///     not get retried every interval forever.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;
}
