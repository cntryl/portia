using Cntryl.Portia;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portia.Accounts;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAccountsApplication(builder.Configuration, builder.Environment);
builder.Services.AddAccountsTelemetry("portia.accounts.worker");
_ = builder.Services.AddPortia().AddWorkers();

using var host = builder.Build();
var workerLogger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Portia.Accounts.Worker");
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
    AccountWorkerLog.Started(workerLogger));
await host.RunAsync();

static partial class AccountWorkerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Account projection worker started")]
    public static partial void Started(ILogger logger);
}
