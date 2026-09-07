namespace Grand.Domain.Shipping;

/// <summary>
///     A carrier tracking event, captured exactly as it was received and processed later.
///
///     Carrier webhooks are the reason this exists. The carrier POSTs a scan event and expects a
///     response within a few seconds; it retries a fixed number of times and then gives up
///     permanently, so a slow or failed request loses the event with no way to backfill. Writing one
///     row here and returning 200 keeps the request fast and makes the event durable, and every
///     consumer - applying it to the Shipment, publishing it to a data lake - reads from this
///     collection on its own schedule.
///
///     It also decouples the delivery mechanism from the consumers. Today a scheduled task drains
///     this collection directly; introducing a broker later changes only that relay, not the
///     controller that writes here nor the contract below.
/// </summary>
public class ShipmentEventOutbox : BaseEntity
{
    /// <summary>
    ///     Contract version of Payload. Carrier formats and this application's entities both change
    ///     over releases, so consumers must be able to tell which shape they are reading rather than
    ///     inferring it.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    ///     Provider system name that captured the event, e.g. Shipping.CarrierTracking.
    /// </summary>
    public string Provider { get; set; }

    /// <summary>
    ///     Carrier-specific event type, normalised where possible - "delivered", "in_transit".
    /// </summary>
    public string EventType { get; set; }

    /// <summary>
    ///     Tracking number the event refers to. The only link back to a Shipment, so a value the
    ///     carrier does not recognise is expected and must not be treated as an error.
    /// </summary>
    public string TrackingNumber { get; set; }

    /// <summary>
    ///     Resolved shipment, when the tracking number matched one. Null means unmatched - keep the
    ///     row rather than discarding it, so the event survives a shipment created out of order.
    /// </summary>
    public string ShipmentId { get; set; }

    public string OrderId { get; set; }
    public string StoreId { get; set; }

    /// <summary>
    ///     When the carrier says the event happened, which is not when it was received.
    /// </summary>
    public DateTime? OccurredUtc { get; set; }

    /// <summary>
    ///     Raw carrier payload, retained verbatim. Reprocessing after a mapping bug is only possible
    ///     if the original is kept, and it is what lands in the data lake's bronze layer.
    /// </summary>
    public string Payload { get; set; }

    /// <summary>
    ///     Set when a consumer has applied the event to the Shipment. Null means outstanding.
    /// </summary>
    public DateTime? ProcessedUtc { get; set; }

    /// <summary>
    ///     Set when the event has been written to the data lake. Tracked separately from
    ///     ProcessedUtc so the two consumers advance independently - a lake outage must not stop
    ///     deliveries being recorded, and vice versa.
    /// </summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>
    ///     Failed attempts. Used to stop retrying a payload that will never parse, instead of
    ///     retrying it every interval forever.
    /// </summary>
    public int AttemptCount { get; set; }

    public string LastError { get; set; }
}
