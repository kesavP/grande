using Grand.Business.Core.Interfaces.Checkout.Orders;
using Grand.Business.Core.Interfaces.Checkout.Shipping;
using Grand.Business.Core.Interfaces.Storage;
using Grand.Business.Core.Interfaces.System.ScheduleTasks;
using Grand.Data;
using Grand.Domain.Shipping;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Grand.Module.ScheduledTasks.BackgroundServices;

/// <summary>
///     Drains ShipmentEventOutbox onto the Shipment records it refers to.
///
///     This is the relay between capture and effect. The webhook endpoint only persists what the
///     carrier sent - all interpretation happens here, off the request path, where a failure can be
///     retried instead of losing an event the carrier will not send again.
///
///     It is also the seam that keeps a message broker optional. Today this applies events directly;
///     publishing them to Service Bus or Event Hubs later replaces this class and nothing else -
///     not the controller, not the contract, not the domain.
/// </summary>
public class ShipmentEventRelayTask(
    IRepository<ShipmentEventOutbox> outboxRepository,
    IShipmentService shipmentService,
    IOrderService orderService,
    IEventLakeWriter lakeWriter,
    ILogger<ShipmentEventRelayTask> logger)
    : IScheduleTask
{
    //bounded batch: an outbox that has grown during an outage must not be loaded in one query
    private const int BatchSize = 200;

    //give up after this many failures so one unparseable payload is not retried forever
    private const int MaxAttempts = 5;

    public async Task Execute()
    {
        await ApplyPending();
        await PublishToLake();
    }

    private async Task ApplyPending()
    {
        var pending = outboxRepository.Table
            .Where(x => x.ProcessedUtc == null && x.AttemptCount < MaxAttempts)
            .OrderBy(x => x.CreatedOnUtc)
            .Take(BatchSize)
            .ToList();

        foreach (var row in pending)
            try
            {
                await Apply(row);
                await outboxRepository.UpdateField(row.Id, x => x.ProcessedUtc, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                //record the attempt in a way the next run can see, then carry on. One bad event
                //must not stop the rest of the batch.
                logger.LogError(ex, "Failed to apply shipment event {OutboxId} for tracking {Tracking}",
                    row.Id, row.TrackingNumber);
                await outboxRepository.UpdateField(row.Id, x => x.AttemptCount, row.AttemptCount + 1);
                await outboxRepository.UpdateField(row.Id, x => x.LastError, ex.Message);
            }
    }

    private async Task Apply(ShipmentEventOutbox row)
    {
        var shipment = await Resolve(row);
        if (shipment == null)
        {
            //Not an error. A callback can arrive before the shipment is saved, so count the attempt
            //and let a later run match it - until MaxAttempts stops it becoming permanent work.
            logger.LogInformation("No shipment matches tracking number {Tracking} yet", row.TrackingNumber);
            await outboxRepository.UpdateField(row.Id, x => x.AttemptCount, row.AttemptCount + 1);
            return;
        }

        //Only "delivered" changes state today. Every other carrier scan is still captured and still
        //visible through IShipmentTracker - it just does not move the shipment on.
        if (!IsDelivered(row.EventType)) return;

        var occurred = row.OccurredUtc ?? DateTime.UtcNow;

        if (!shipment.DeliveryDateUtc.HasValue)
        {
            shipment.DeliveryDateUtc = occurred;
            await shipmentService.UpdateShipment(shipment);
        }

        await MarkOrderDelivered(shipment.OrderId);
    }

    private async Task<Shipment> Resolve(ShipmentEventOutbox row)
    {
        if (!string.IsNullOrEmpty(row.ShipmentId))
            return await shipmentService.GetShipmentById(row.ShipmentId);

        if (string.IsNullOrEmpty(row.TrackingNumber)) return null;

        var matches = await shipmentService.GetAllShipments(
            trackingNumber: row.TrackingNumber, pageIndex: 0, pageSize: 1);
        return matches.FirstOrDefault();
    }

    private async Task MarkOrderDelivered(string orderId)
    {
        if (string.IsNullOrEmpty(orderId)) return;

        var order = await orderService.GetOrderById(orderId);
        if (order == null) return;

        //An order can ship in several parcels, so it is only Delivered once every shipment is.
        //Anything outstanding leaves it PartiallyShipped.
        //GetShipmentsByOrder, not GetAllShipments with an unbounded page size and an in-memory
        //filter - the latter fetches every shipment in the installation to look at a handful.
        var forOrder = await shipmentService.GetShipmentsByOrder(orderId);
        var allDelivered = forOrder.Count > 0 && forOrder.All(x => x.DeliveryDateUtc.HasValue);

        var target = allDelivered ? ShippingStatus.Delivered : ShippingStatus.PartiallyShipped;
        if (order.ShippingStatusId == target) return;

        order.ShippingStatusId = target;
        await orderService.UpdateOrder(order);
    }

    /// <summary>
    ///     Publishes captured events to the lake's bronze layer.
    ///
    ///     A separate pass over a separate marker (PublishedUtc, not ProcessedUtc) on purpose: the
    ///     two consumers must not be able to block each other. A lake outage cannot stop deliveries
    ///     being recorded, and a shipment that never matches must still reach analytics.
    /// </summary>
    private async Task PublishToLake()
    {
        if (!lakeWriter.Enabled) return;

        var unpublished = outboxRepository.Table
            .Where(x => x.PublishedUtc == null)
            .OrderBy(x => x.CreatedOnUtc)
            .Take(BatchSize)
            .ToList();

        if (unpublished.Count == 0) return;

        //One object per event date, so a batch spanning midnight does not land in the wrong dt=
        //partition and force a reader to scan both days to find one event.
        foreach (var day in unpublished.GroupBy(x => (x.OccurredUtc ?? x.CreatedOnUtc).Date))
        {
            var lines = day.Select(ToBronzeLine).ToList();

            try
            {
                await lakeWriter.AppendBatch("shipment_events", day.Key, lines);

                foreach (var row in day)
                    await outboxRepository.UpdateField(row.Id, x => x.PublishedUtc, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                //Leave PublishedUtc null and stop. Marking them published after a failed write
                //would lose the events permanently - bronze is the only copy analytics ever sees.
                logger.LogError(ex, "Failed publishing {Count} shipment events for {Date:yyyy-MM-dd}",
                    lines.Count, day.Key);
                return;
            }
        }
    }

    /// <summary>
    ///     Envelope written to bronze: identifiers, timings and the untouched carrier payload.
    ///     Deliberately not the Shipment entity - entity schemas drift between releases with no
    ///     compatibility contract, and a downstream table has no IgnoreExtraElements to protect it.
    /// </summary>
    private static string ToBronzeLine(ShipmentEventOutbox row) =>
        JsonSerializer.Serialize(new {
            event_id = row.Id,
            schema_version = row.SchemaVersion,
            provider = row.Provider,
            event_type = row.EventType,
            tracking_number = row.TrackingNumber,
            shipment_id = row.ShipmentId,
            order_id = row.OrderId,
            store_id = row.StoreId,
            occurred_utc = row.OccurredUtc,
            captured_utc = row.CreatedOnUtc,
            //raw, as a string - parsing it here would defeat the point of a raw layer
            payload = row.Payload
        });

    private static bool IsDelivered(string eventType) =>
        !string.IsNullOrEmpty(eventType) &&
        eventType.Equals("delivered", StringComparison.OrdinalIgnoreCase);
}
