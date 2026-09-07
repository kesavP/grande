using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shipping.CarrierTracking.Services;

namespace Shipping.CarrierTracking.Controllers;

/// <summary>
///     Receives carrier delivery callbacks.
///
///     Does the minimum possible synchronously: verify, persist, respond. Carriers time out in a few
///     seconds and retry a fixed number of times before giving up permanently, so anything slow or
///     failure-prone here loses events with no way to recover them. All interpretation happens on
///     the relay.
/// </summary>
public class CarrierWebhookController(
    ICarrierWebhookService webhookService,
    ILogger<CarrierWebhookController> logger)
    : Controller
{
    private const string SignatureHeader = "X-Carrier-Signature";

    //anonymous by necessity - the carrier cannot authenticate. The HMAC signature is what makes
    //this safe, which is why an unconfigured secret rejects rather than accepts.
    [AllowAnonymous]
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive()
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body))
        {
            //the signature is over the exact bytes sent; re-serialising a bound model would
            //change them and every signature check would fail
            rawBody = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrEmpty(rawBody)) return BadRequest();

        if (!webhookService.VerifySignature(rawBody, Request.Headers[SignatureHeader]))
        {
            logger.LogWarning("Carrier webhook rejected: signature mismatch");
            return Unauthorized();
        }

        try
        {
            await webhookService.Capture(rawBody);
        }
        catch (Exception ex)
        {
            //500 tells the carrier to retry. Returning 200 on a failed capture would make it
            //believe the event was delivered and it would never be sent again.
            logger.LogError(ex, "Carrier webhook could not be captured");
            return StatusCode(500);
        }

        return Ok();
    }
}
