using Cntryl.Portia.Testing;
using Cntryl.Portia.Features.Orders.Aggregates;

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
