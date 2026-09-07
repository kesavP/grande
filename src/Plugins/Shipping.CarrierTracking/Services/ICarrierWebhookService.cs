namespace Shipping.CarrierTracking.Services;

public interface ICarrierWebhookService
{
    /// <summary>
    ///     Verifies the signature over the raw request body.
    /// </summary>
    bool VerifySignature(string rawBody, string signatureHeader);

    /// <summary>
    ///     Persists the callback to the outbox. Does no shipment work - that happens on the relay.
    /// </summary>
    Task Capture(string rawBody);
}
