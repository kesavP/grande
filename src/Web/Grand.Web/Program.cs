using Grand.Web.Common.Extensions;
using Grand.Web.Common.Startup;
using StartupBase = Grand.Infrastructure.StartupBase;

var builder = WebApplication.CreateBuilder(args);

//add configuration
builder.Configuration.AddAppSettingsJsonFile(args);

builder.AddServiceDefaults();

builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateScopes = false;
    options.ValidateOnBuild = false;
});

//add services
StartupBase.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

builder.ConfigureApplicationSettings();

//"--run-task <name>" executes a single scheduled task and exits, for an external scheduler
//(a Container Apps job, a Kubernetes CronJob) to own the cadence. The in-process polling loop
//only advances while a web instance is running, so a deployment that scales to zero silently
//stops doing background work.
var taskToRun = ScheduleTaskRunner.GetTaskName(args);

//register the polling loop only for a normal start - hosting it in a one-shot run would start
//every other task's loop as well and then tear it down mid-execution
if (taskToRun is null)
    builder.Services.RegisterTasks(builder.Configuration);

//build app
var app = builder.Build();

if (taskToRun is not null)
    //no request pipeline and no Kestrel: nothing here serves HTTP. The exit code is the result.
    return await ScheduleTaskRunner.RunOnce(app.Services, taskToRun);

//request pipeline
StartupBase.ConfigureRequestPipeline(app, builder.Environment);

//run app
await app.RunAsync();

return 0;
