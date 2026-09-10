using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>The ambient dependencies a Fitz worker needs to build its runner.</summary>
sealed record FitzWorkerHost(
    Client Client,
    IServiceScopeFactory Scopes,
    IRequestDeserializer Serializer,
    TimeProvider Clock,
    ILogger<FitzRequestQueueConsumer>? QueueLogger,
    ILogger<QueueRunner>? QueueRunnerLogger,
    ILogger<RequestNotificationRunner>? NotificationLogger);
