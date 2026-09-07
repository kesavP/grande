using Grand.Business.Core.Interfaces.Checkout.Shipping;
using Grand.Data;
using Grand.Domain.Shipping;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shipping.CarrierTracking.Models;

namespace Shipping.CarrierTracking.Services;

public class CarrierWebhookService(
    IRepository<ShipmentEventOutbox> outboxRepository,
    IShipmentService shipmentService,
    CarrierTrackingSettings settings,
    ILogger<CarrierWebhookService> logger)
    : ICarrierWebhookService
{
    public bool VerifySignature(string rawBody, string signatureHeader)
    {
        if (string.IsNullOrEmpty(settings.SigningSecret))
        {
            //refuse rather than accept everything - an unconfigured secret must not mean "open"
            logger.LogError("Carrier webhook rejected: no signing secret is configured");
            return false;
        }

        if (string.IsNullOrEmpty(signatureHeader)) return false;

        var expected = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SigningSecret),
                Encoding.UTF8.GetBytes(rawBody)));

        //fixed-time comparison - a plain string compare leaks the signature one byte at a time
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader.Trim()));
    }

    public async Task Capture(string rawBody)
    {
        CarrierWebhookPayload payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<CarrierWebhookPayload>(rawBody);
        }
        catch (JsonException ex)
        {
            //store it anyway. An unparseable payload is a mapping problem to investigate, not a
            //reason to discard an event the carrier will never send again.
            logger.LogWarning(ex, "Carrier webhook payload could not be parsed - captured raw");
        }

        var outbox = new ShipmentEventOutbox {
            SchemaVersion = 1,
            Provider = CarrierTrackingDefaults.ProviderSystemName,
            EventType = payload?.Status,
            TrackingNumber = payload?.TrackingNumber,
            OccurredUtc = payload?.OccurredAt,
            Payload = rawBody
        };

        //resolve the shipment now if we can, so the relay does not have to. An unmatched tracking
        //number is normal - the callback can arrive before the shipment is saved - so leave the
        //ids null and let the relay try again.
        if (!string.IsNullOrEmpty(outbox.TrackingNumber))
        {
            var shipment = await FindByTrackingNumber(outbox.TrackingNumber);
            if (shipment != null)
            {
                outbox.ShipmentId = shipment.Id;
                outbox.OrderId = shipment.OrderId;
                outbox.StoreId = shipment.StoreId;
            }
        }

        await outboxRepository.InsertAsync(outbox);
    }

    private async Task<Shipment> FindByTrackingNumber(string trackingNumber)
    {
        var shipments = await shipmentService.GetAllShipments(trackingNumber: trackingNumber,
            pageIndex: 0, pageSize: 1);
        return shipments.FirstOrDefault();
    }
}
