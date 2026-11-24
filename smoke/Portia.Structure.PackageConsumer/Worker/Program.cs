using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cntryl.Portia.DataProtection;
using Cntryl.Portia.Features.Orders;
using Cntryl.Portia.Features.Orders.Aggregates;
using Cntryl.Portia.Features.Orders.Commands;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Arrange
var orderId = Uuid.CreateVersion4();
var firstLineId = Uuid.CreateVersion4();
var secondLineId = Uuid.CreateVersion4();
var scenario = new AggregateScenario<Order>(new Order(orderId));

// Act
var accepted = scenario.WhenCommitOnSuccess(order => order.ChangeLineQuantity(firstLineId, 7));
var acceptedFact = scenario.PendingEvents.Count == 0 ? null : scenario.PendingEvents[0];
var retry = scenario.WhenCommitOnSuccess(order => order.ChangeLineQuantity(firstLineId, 7));
var rejected = scenario.WhenCommitOnSuccess(order => order.ChangeLineQuantity(secondLineId, 6));
var replay = acceptedFact is null
    ? null
    : new AggregateScenario<Order>(new Order(orderId)).Given(acceptedFact);
var replayedLine = replay?.Aggregate.Lines.SingleOrDefault();

// Assert
if (!accepted.IsSuccess || acceptedFact is null || !retry.IsSuccess || rejected.IsSuccess ||
    scenario.CommittedEventCount != 1 || scenario.PendingEvents.Count != 0 || replay?.Aggregate.TotalQuantity != 7 ||
    replayedLine?.Id != firstLineId || replayedLine.Quantity != 7)
    throw new InvalidOperationException($"Packed aggregate proof failed: accepted={accepted.IsSuccess} ({accepted.Error?.Message}), fact={acceptedFact is not null}, retry={retry.IsSuccess} ({retry.Error?.Message}), rejected={rejected.IsSuccess}, committed={scenario.CommittedEventCount}, pending={scenario.PendingEvents.Count}, replayTotal={replay?.Aggregate.TotalQuantity}, replayLine={replayedLine?.Id}, replayQuantity={replayedLine?.Quantity}.");

var directory = Directory.CreateTempSubdirectory("portia-structure-protection-");
var reference = "synthetic order reference"u8.ToArray();

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        EnvironmentName = Environments.Development,
    });
    var path = Path.Combine(directory.FullName, "private", "keys.json");
    DevelopmentDataProtectionKeyRingInitializer.Initialize(path, builder.Environment);
    builder.Services.AddDevelopmentDataProtection(path);
    builder.Services.AddPortia().AddOrders();

    using var host = builder.Build();
    await host.StartAsync();

    await using var scope = host.Services.CreateAsyncScope();
    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
    var result = await bus.SendAsync(new ProtectOrderReference(orderId, reference), RequestActor.System);

    if (!result.IsSuccess)
        throw new InvalidOperationException("The packed data protection handler did not complete.");

    var protector = scope.ServiceProvider.GetRequiredService<IPayloadProtector>();
    using var opened = await protector.UnprotectAsync(result.Value, "orders.private-reference",
        Encoding.UTF8.GetBytes(orderId.ToString()));

    if (!opened.Plaintext.Span.SequenceEqual(reference))
        throw new InvalidOperationException("The packed data protection handler produced an unreadable value.");

    await host.StopAsync();
    Console.WriteLine("Packed Portia request handler used the host's data protection provider.");
}
finally
{
    CryptographicOperations.ZeroMemory(reference);
    directory.Delete(recursive: true);
}
