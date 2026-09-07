using Grand.Data;
using Grand.Domain.Shipping;
using Grand.Domain.Tasks;
using Grand.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grand.Module.Migration.Migrations._2._4;

/// <summary>
///     Brings an existing installation up to the state a fresh install gets from
///     InstallDataScheduleTasks and CreateIndexes.
///
///     Without this, an upgraded store has the carrier-tracking plugin, the outbox collection and
///     the relay class, but no ScheduleTask row to run it - so captured events would accumulate
///     forever and never be applied, silently.
/// </summary>
public class MigrationShipmentEventOutbox : IMigration
{
    private const string TaskName = "Apply carrier shipment events";

    public int Priority => 2;
    public DbVersion Version => new(2, 4);
    public Guid Identity => new("72D3818E-7DC8-417E-8EC3-397DCFBC57D5");
    public string Name => "Add the carrier shipment event relay task and outbox indexes";

    public bool UpgradeProcess(IServiceProvider serviceProvider)
    {
        AddRelayTask(serviceProvider);
        AddOutboxIndexes(serviceProvider);
        return true;
    }

    private static void AddRelayTask(IServiceProvider serviceProvider)
    {
        var repository = serviceProvider.GetRequiredService<IRepository<ScheduleTask>>();
        var logService = serviceProvider.GetRequiredService<ILogger<MigrationShipmentEventOutbox>>();

        try
        {
            //idempotent: running the migration twice must not create a second task, which would
            //make two runners compete for the same outbox rows
            if (repository.Table.Any(x => x.ScheduleTaskName == TaskName)) return;

            repository.Insert(new ScheduleTask {
                //must equal the DI key in Grand.Module.ScheduledTasks or the runner cannot resolve it
                ScheduleTaskName = TaskName,
                Enabled = false,
                StopOnError = false,
                TimeInterval = 5
            });
        }
        catch (Exception ex)
        {
            logService.LogError(ex, "UpgradeProcess - AddRelayTask (2.4)");
        }
    }

    private static void AddOutboxIndexes(IServiceProvider serviceProvider)
    {
        var repository = serviceProvider.GetRequiredService<IRepository<ShipmentEventOutbox>>();
        var databaseContext = serviceProvider.GetRequiredService<IDatabaseContext>();
        var logService = serviceProvider.GetRequiredService<ILogger<MigrationShipmentEventOutbox>>();

        try
        {
            //The relay scans for outstanding work every few minutes and the collection grows with
            //every carrier callback, so all three of its query shapes need support. Ascending on
            //CreatedOnUtc because both passes order by it oldest-first.
            databaseContext.CreateIndex(repository,
                OrderBuilder<ShipmentEventOutbox>.Create()
                    .Ascending(x => x.ProcessedUtc).Ascending(x => x.CreatedOnUtc),
                "Outbox_Processed").GetAwaiter().GetResult();

            databaseContext.CreateIndex(repository,
                OrderBuilder<ShipmentEventOutbox>.Create()
                    .Ascending(x => x.PublishedUtc).Ascending(x => x.CreatedOnUtc),
                "Outbox_Published").GetAwaiter().GetResult();

            //IShipmentTracker reads the history for one consignment
            databaseContext.CreateIndex(repository,
                OrderBuilder<ShipmentEventOutbox>.Create().Ascending(x => x.TrackingNumber),
                "Outbox_TrackingNumber").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logService.LogError(ex, "UpgradeProcess - AddOutboxIndexes (2.4)");
        }
    }
}
