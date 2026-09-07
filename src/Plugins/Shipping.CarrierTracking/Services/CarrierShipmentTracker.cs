using Grand.Business.Core.Enums.Checkout;
using Grand.Business.Core.Interfaces.Checkout.Shipping;
using Grand.Data;
using Grand.Domain.Shipping;
using System.Text.Json;
using Shipping.CarrierTracking.Models;

namespace Shipping.CarrierTracking.Services;

/// <summary>
///     The pull side of tracking: a deep link, and the events already captured for a consignment.
///
///     IShipmentTracker models pull only, so this reads what the webhook has stored rather than
///     calling the carrier per request. That keeps a customer-facing page off a third-party API on
///     the render path, and means the history survives even if the carrier expires it.
/// </summary>
public class CarrierShipmentTracker(
    IRepository<ShipmentEventOutbox> outboxRepository,
    CarrierTrackingSettings settings)
    : IShipmentTracker
{
    public Task<string> GetUrl(string trackingNumber)
    {
        var template = settings.TrackingUrlTemplate;
        return Task.FromResult(string.IsNullOrEmpty(template) || string.IsNullOrEmpty(trackingNumber)
            ? string.Empty
            : string.Format(template, trackingNumber));
    }

    public Task<IList<ShipmentStatusEvent>> GetShipmentEvents(string trackingNumber)
    {
        if (string.IsNullOrEmpty(trackingNumber))
            return Task.FromResult<IList<ShipmentStatusEvent>>(new List<ShipmentStatusEvent>());

        var rows = outboxRepository.Table
            .Where(x => x.TrackingNumber == trackingNumber)
            .OrderBy(x => x.OccurredUtc)
            .ToList();

        IList<ShipmentStatusEvent> events = rows.Select(Map).Where(x => x != null).ToList();
        return Task.FromResult(events);
    }

    private static ShipmentStatusEvent Map(ShipmentEventOutbox row)
    {
        CarrierWebhookPayload payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<CarrierWebhookPayload>(row.Payload);
        }
        catch (JsonException)
        {
            //a row captured from an unparseable payload still exists on purpose; skip it here
            //rather than failing the whole history
        }

        if (payload == null) return null;

        return new ShipmentStatusEvent {
            EventName = payload.Status,
            Location = payload.Location,
            CountryCode = payload.CountryCode,
            Date = payload.OccurredAt ?? row.OccurredUtc
        };
    }
}
