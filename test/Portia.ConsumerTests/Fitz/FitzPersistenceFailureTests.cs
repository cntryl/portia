using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class FitzPersistenceFailureTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);

    [Theory]
    [InlineData("append", false)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("success", false)]
    public async Task SessionAlwaysDisposesWhileFailurePreservesOriginalErrorAndPendingBatch(string failureAt,
        bool cleanupFails)
    {
        var session = new Session(failureAt, cleanupFails);
        var streams = new Streams(session);
        var store = new FitzEventStore(streams,
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(store);
        _ = services.AddPortia();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.Aggregates();
        var account = new Account(Uuid.CreateVersion4());
        var payload = new Declined("failure contract");
        account.Audit(payload);
        var scenario = new AggregateScenario<Account>(account);

        if (failureAt == "success")
        {
            await repository.SaveAsync(account, _saveContext);
            Assert.Empty(scenario.PendingAudits);
            Assert.Equal(0, session.Rollbacks);
        }
        else
        {
            var error =
                await Assert.ThrowsAsync<IOException>(() => repository.SaveAsync(account, _saveContext).AsTask());
            Assert.Same(session.Failure, error);
            Assert.Same(payload, Assert.Single(scenario.PendingAudits));
            Assert.Equal(1, session.Rollbacks);
        }

        Assert.Equal(0UL, account.CommittedStreamPosition);
        Assert.Equal(4,
            Uuid.Parse(EventStreamAddress.Parse(streams.Route!).Resource, CultureInfo.InvariantCulture).Version);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("append", FitzErrorCodes.StreamConcurrencyConflict, "unrelated wording", true)]
    [InlineData("commit", FitzErrorCodes.StreamConcurrencyConflict, "unrelated wording", true)]
    [InlineData("append", FitzErrorCodes.StreamSessionAlreadyActive, "concurrency conflict", false)]
    [InlineData("commit", null, "concurrency conflict", false)]
    public async Task OnlyStructuredConflictCodeIsTranslatedDespiteCleanupFailures(string failureAt, uint? code,
        string message, bool conflict)
    {
        var original = new StreamException(message, "APPEND_FAILED", domainCode: code);
        var session = new Session(failureAt, true) { Failure = original };
        var store = new FitzEventStore(new Streams(session),
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        var payload = new Declined("pending");
        account.Audit(payload);
        var error = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());
        if (conflict)
        {
            Assert.Same(original, Assert.IsType<EventStreamConcurrencyException>(error).InnerException);
        }
        else
        {
            Assert.Same(original, error);
        }

        Assert.Same(payload, Assert.Single(new AggregateScenario<Account>(account).PendingAudits));
        Assert.Equal(1, session.Rollbacks);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task ShouldTranslateConcurrencyExceptionGivenFitzRejectsAppendSessionAdmission()
    {
        // Arrange
        var original = new StreamException("append session already active", "BEGIN_FAILED",
            domainCode: FitzErrorCodes.StreamSessionAlreadyActive);
        var session = new Session("success", false);
        var store = new FitzEventStore(new Streams(session, original),
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        var payload = new Declined("pending");
        account.Audit(payload);

        // Act
        var error = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() =>
            repository.SaveAsync(account, _saveContext).AsTask());

        // Assert
        Assert.Same(original, error.InnerException);
        Assert.Same(payload, Assert.Single(new AggregateScenario<Account>(account).PendingAudits));
        Assert.Equal(0, session.Rollbacks);
        Assert.False(session.Disposed);
    }

    // A stream route is realm/area/resource: for a tenant's aggregate, its tenant and aggregate ID.
    // These failures reach error logs and trace exception events, so the message names the stream
    // area and expected position instead.
    [Theory]
    [InlineData(FitzErrorCodes.StreamSessionAlreadyActive)]
    [InlineData(FitzErrorCodes.StreamConcurrencyConflict)]
    public async Task ConcurrencyFailuresKeepTenantAndAggregateIdentifiersOutOfTheirMessage(uint code)
    {
        var original = new StreamException("rejected", "APPEND_FAILED", domainCode: code);
        var admission = code == FitzErrorCodes.StreamSessionAlreadyActive;
        var session = new Session(admission ? "success" : "append", false) { Failure = original };
        var streams = new Streams(session, admission ? original : null);
        var store = new FitzEventStore(streams,
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        var account = new Account(Uuid.CreateVersion4());
        account.Audit(new Declined("pending"));

        var error = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() =>
            new AggregateRepository(store).SaveAsync(account, _saveContext).AsTask());

        var stream = EventStreamAddress.Parse(streams.Route!);
        Assert.Contains(stream.Area, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stream.Realm, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stream.Resource, error.Message, StringComparison.Ordinal);
    }

    sealed class Session(string failureAt, bool cleanupFails) : IStreamSession
    {
        public Exception Failure { get; init; } = new IOException("Injected session failure");

        public int Rollbacks { get; private set; }

        public bool Disposed { get; private set; }

        public Task<ulong?> AppendAsync(ulong expectedOffset, ReadOnlyMemory<byte> body,
            ReadOnlyMemory<byte>? metadata = null, string? discriminator = null, CancellationToken ct = default)
            => failureAt == "append" ? throw Failure : Task.FromResult<ulong?>(expectedOffset);

        public Task CommitAsync(CancellationToken ct = default)
            => failureAt == "commit" ? throw Failure : Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct = default)
        {
            Rollbacks++;
            return cleanupFails ? throw new IOException("Rollback failed") : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return cleanupFails ? throw new IOException("Dispose failed") : ValueTask.CompletedTask;
        }
    }

    sealed class Streams(IStreamSession session, Exception? beginFailure = null) : IStreamClient
    {
        public string? Route { get; private set; }

        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default)
        {
            Route = route;
            if (beginFailure is not null)
                return Task.FromException<IStreamSession>(beginFailure);
            return Task.FromResult(session);
        }

        public IAsyncEnumerable<StreamRecord> ReadAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StreamReadPage> ReadPageAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
