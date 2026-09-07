namespace Shipping.CarrierTracking;

public static class CarrierTrackingDefaults
{
    public const string ProviderSystemName = "Shipping.CarrierTracking";
    public const string FriendlyName = "Shipping.CarrierTracking.FriendlyName";

    /// <summary>
    ///     Route the carrier POSTs to. Registered by EndpointProvider.
    /// </summary>
    public const string WebhookRoute = "shipping/carrier/webhook";

    public const string ConfigurationUrl = "/Admin/CarrierTracking/Configure";
}
