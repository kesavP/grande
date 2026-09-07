using Grand.Infrastructure.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Shipping.CarrierTracking;

public class EndpointProvider : IEndpointProvider
{
    public void RegisterEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapControllerRoute("Plugin.Shipping.CarrierTracking.Webhook",
            CarrierTrackingDefaults.WebhookRoute,
            new { controller = "CarrierWebhook", action = "Receive" });
    }

    public int Priority => 10;
}
