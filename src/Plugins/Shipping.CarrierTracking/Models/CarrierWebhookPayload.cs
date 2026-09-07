using System.Text.Json.Serialization;

namespace Shipping.CarrierTracking.Models;

/// <summary>
///     The subset of a carrier callback this plugin understands.
///
///     Deliberately not the carrier's full schema: only the fields that drive a decision are mapped,
///     and the untouched original is stored on the outbox row. A carrier adding or renaming a field
///     therefore cannot break ingestion - at worst a new event type is not recognised, and the raw
///     payload is still captured for reprocessing.
/// </summary>
public class CarrierWebhookPayload
{
    [JsonPropertyName("tracking_number")] public string TrackingNumber { get; set; }

    /// <summary>Carrier's own status string, e.g. "delivered", "out_for_delivery".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("occurred_at")] public DateTime? OccurredAt { get; set; }

    [JsonPropertyName("location")] public string Location { get; set; }

    [JsonPropertyName("country_code")] public string CountryCode { get; set; }
}
