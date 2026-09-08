using Grand.Domain.Tasks;

namespace Grand.Module.Installer.Services;

public partial class InstallationService
{
    /// <summary>
    ///     Seeds the scheduled tasks, all disabled.
    ///
    ///     Every task is opt-in: an administrator enables the ones a given store needs from
    ///     System -> Scheduled tasks. Nothing runs on a fresh installation until someone decides
    ///     it should, which keeps a new store from silently deleting guest records, calling an
    ///     external exchange-rate service or emailing customers before it is configured.
    ///
    ///     The Enabled flag is authoritative for both execution paths - the in-process
    ///     BackgroundServiceTask loop and ScheduleTaskRunner (--run-task), which reads the same
    ///     row - so the admin switch turns a task off everywhere, including scheduled jobs.
    ///
    ///     Note "Send emails" is disabled here too. Until it is enabled, order confirmations and
    ///     password resets accumulate in the QueuedEmail collection unsent.
    /// </summary>
    protected virtual Task InstallScheduleTasks()
    {
        //these tasks are default - they are created in order to insert them into database
        //and nothing above it
        //there is no need to send arguments into ctor - all are null
        var tasks = new List<ScheduleTask> {
            new() {
                ScheduleTaskName = "Send emails",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 1
            },
            new() {
                ScheduleTaskName = "Delete guests",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 1440
            },
            new() {
                ScheduleTaskName = "Clear cache",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 120
            },
            new() {
                ScheduleTaskName = "Update currency exchange rates",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 1440
            },
            new() {
                ScheduleTaskName = "Generate sitemap XML file",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 10080
            },
            new() {
                ScheduleTaskName = "End of the auctions",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 60
            },
            new() {
                ScheduleTaskName = "Cancel unpaid and pending orders",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 1440
            },
            new ScheduleTask {
                //Key must equal the DI registration in Grand.Module.ScheduledTasks.
                //Disabled by default: it does nothing until Shipping.CarrierTracking is installed
                //and a carrier is actually posting webhooks.
                ScheduleTaskName = "Apply carrier shipment events",
                Enabled = false,
                StopOnError = false,
                TimeInterval = 5
            }
        };
        tasks.ForEach(x => _scheduleTaskRepository.Insert(x));
        return Task.CompletedTask;
    }
}