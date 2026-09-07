using Grand.Business.Core.Interfaces.System.ScheduleTasks;
using Grand.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grand.Web.Common.Startup;

/// <summary>
///     Runs one scheduled task to completion and exits, instead of hosting the polling loop.
///
///     The loop in BackgroundServiceTask only advances while a web instance is running. On any
///     platform that scales to zero - Container Apps, Kubernetes with KEDA - an idle deployment
///     stops sending queued email, expiring unpaid orders and draining outbox collections, and
///     nothing reports that it has stopped. Splitting a single run out of the host lets an external
///     scheduler own the cadence and surface failures as a non-zero exit code.
///
///     Running alongside a web instance is safe: ScheduleTaskService.TryClaimTaskRun does an atomic
///     compare-and-set on LastStartUtc, so whichever process claims a run first executes it and the
///     other stands down. The lease was already there for multi-replica hosting; this reuses it.
/// </summary>
public static class ScheduleTaskRunner
{
    public const string ArgumentName = "--run-task";

    /// <summary>
    ///     Task name from the command line, or null when the process should start normally.
    /// </summary>
    public static string GetTaskName(string[] args)
    {
        if (args is null) return null;

        var index = Array.IndexOf(args, ArgumentName);
        if (index < 0 || index + 1 >= args.Length) return null;

        var name = args[index + 1];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    ///     Executes the named task once.
    /// </summary>
    /// <returns>
    ///     Process exit code. 0 succeeded or was deliberately skipped, 1 failed. A scheduler treats
    ///     any non-zero as a failed run, so "disabled" and "not seeded" must not be failures - they
    ///     are states an operator chose, not faults.
    /// </returns>
    public static async Task<int> RunOnce(IServiceProvider services, string taskName)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskName);

        //IScheduleTask is registered scoped and keyed on the task name; the key must equal the
        //ScheduleTaskName stored in the database, which is the contract TaskHandler relies on too
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ScheduleTaskRunner));

        if (!DataSettingsManager.DatabaseIsInstalled())
        {
            logger.LogError("Cannot run '{TaskName}': the database is not configured", taskName);
            return 1;
        }

        var scheduleTaskService = provider.GetRequiredService<IScheduleTaskService>();
        var record = await scheduleTaskService.GetTaskByName(taskName);

        if (record == null)
        {
            //the row is seeded by the installer or a migration; a scheduler that starts before
            //either has run should not be treated as broken
            logger.LogWarning("Task '{TaskName}' is not present in the database - nothing to run", taskName);
            return 0;
        }

        if (!record.Enabled)
        {
            //honour the admin panel switch, so disabling a task there stops it everywhere rather
            //than only in the web host
            logger.LogInformation("Task '{TaskName}' is disabled - skipping", taskName);
            return 0;
        }

        var task = provider.GetKeyedService<IScheduleTask>(taskName);
        if (task == null)
        {
            //a seeded row whose key matches no registration is a genuine defect - the runner would
            //silently do nothing on every schedule
            logger.LogError(
                "Task '{TaskName}' has no registered implementation. The DI key must exactly match ScheduleTaskName",
                taskName);
            return 1;
        }

        try
        {
            logger.LogInformation("Running task '{TaskName}'", taskName);
            await task.Execute();

            record.LastSuccessUtc = DateTime.UtcNow;
            record.LastStartUtc = DateTime.UtcNow;
            await scheduleTaskService.UpdateTask(record);

            logger.LogInformation("Task '{TaskName}' completed", taskName);
            return 0;
        }
        catch (Exception ex)
        {
            //recorded so the admin grid shows the failure, then surfaced as a non-zero exit so the
            //scheduler can alert - logging alone would make a silently failing job look healthy
            logger.LogError(ex, "Task '{TaskName}' failed", taskName);
            try
            {
                record.LastNonSuccessEndUtc = DateTime.UtcNow;
                await scheduleTaskService.UpdateTask(record);
            }
            catch (Exception updateEx)
            {
                logger.LogError(updateEx, "Could not record the failure of '{TaskName}'", taskName);
            }

            return 1;
        }
    }
}
