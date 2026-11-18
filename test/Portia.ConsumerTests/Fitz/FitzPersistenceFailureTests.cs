using System.Diagnostics.Metrics;
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
    public async Task ShouldDisposeSessionAndPreserveErrorGivenFailure(string failureAt,
        bool cleanupFails)
    {
        // Arrange
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

        // Act
        var error = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());

        // Assert
        if (failureAt == "success")
        {
            Assert.Null(error);
            Assert.Empty(scenario.PendingAudits);
            Assert.Equal(0, session.Rollbacks);
        }
        else
        {
            if (failureAt == "commit")
                error = Assert.IsType<EventStreamCommitUnknownException>(error).InnerException;
            Assert.Same(session.Failure, error);
            Assert.Same(payload, Assert.Single(scenario.PendingAudits));
            Assert.Equal(1, session.Rollbacks);
        }

        Assert.Equal(0UL, account.CommittedStreamPosition);
        Assert.Equal(4,
            Uuid.Parse(EventStreamAddress.Parse(streams.Route!).Resource, CultureInfo.InvariantCulture).Version);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task ShouldRemainSuccessfulGivenConfirmedCommitWhenSessionDisposalFails()
    {
        // Arrange
        var session = new Session("success", cleanupFails: true);
        var store = new FitzEventStore(new Streams(session),
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        var account = new Account(Uuid.CreateVersion4());
        account.Audit(new Declined("committed before cleanup failure"));
        using var listener = ListenToCleanupFaults(out var faults);

        // Act
        await new AggregateRepository(store).SaveAsync(account, _saveContext);

        // Assert
        Assert.Empty(new AggregateScenario<Account>(account).PendingAudits);
        Assert.True(session.Committed);
        Assert.Equal(0, session.Rollbacks);
        Assert.True(session.Disposed);
        var fault = Assert.Single(faults);
        Assert.Equal(nameof(FitzEventStore), fault.Single(tag => tag.Key == "portia.runner.name").Value);
        Assert.Equal("cleanup", fault.Single(tag => tag.Key == "portia.stage").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldConfirmTheWholeBatchGivenLostCommitReply(bool readFailsInitially)
    {
        // Arrange
        var session = new Session("lost-reply", false);
        var streams = new Streams(session) { ReadFails = readFailsInitially };
        var store = new FitzEventStore(streams,
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Deposited>(1, "Deposited")));
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(10);
        account.Deposit(5);
        Exception? unconfirmed = null;
        if (readFailsInitially)
        {
            unconfirmed = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());
            streams.ReadFails = false;
        }

        // Act
        await repository.SaveAsync(account, _saveContext);

        // Assert
        if (readFailsInitially)
            Assert.Same(session.Failure, Assert.IsType<EventStreamCommitUnknownException>(unconfirmed).InnerException);
        Assert.Equal(2UL, account.CommittedStreamPosition);
        Assert.Single(streams.SuccessfulReads);
        Assert.Equal(2, session.Appended.Count);
    }

    [Fact]
    public async Task ShouldPreserveConflictGivenOnlyAPrefixOfTheBatchMatches()
    {
        // Arrange
        var session = new Session("lost-reply", false);
        var streams = new Streams(session) { TruncateRead = true };
        var store = new FitzEventStore(streams,
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Deposited>(1, "Deposited")));
        var account = new Account(Uuid.CreateVersion4());
        account.Deposit(10);
        account.Deposit(5);
        var repository = new AggregateRepository(store);
        _ = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());

        // Act
        var failure = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());

        // Assert
        Assert.IsType<EventStreamConcurrencyException>(failure);
        Assert.Equal(0UL, account.CommittedStreamPosition);
        Assert.Equal(2, new AggregateScenario<Account>(account).PendingEvents.Count);
    }

    [Theory]
    [InlineData("append", FitzErrorCodes.StreamConcurrencyConflict, "unrelated wording", true)]
    [InlineData("commit", FitzErrorCodes.StreamConcurrencyConflict, "unrelated wording", true)]
    [InlineData("append", FitzErrorCodes.StreamSessionAlreadyActive, "concurrency conflict", false)]
    [InlineData("commit", null, "concurrency conflict", false)]
    public async Task ShouldTranslateOnlyStructuredConflictCodeGivenCleanupFailures(string failureAt, uint? code,
        string message, bool conflict)
    {
        // Arrange
        var original = new StreamException(message, "APPEND_FAILED", domainCode: code);
        var session = new Session(failureAt, true) { Failure = original };
        var store = new FitzEventStore(new Streams(session),
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var repository = new AggregateRepository(store);
        var account = new Account(Uuid.CreateVersion4());
        var payload = new Declined("pending");
        account.Audit(payload);

        // Act
        var error = await Record.ExceptionAsync(() => repository.SaveAsync(account, _saveContext).AsTask());

        // Assert
        if (conflict)
        {
            Assert.Same(original, Assert.IsType<EventStreamConcurrencyException>(error).InnerException);
        }
        else if (failureAt == "commit")
        {
            Assert.Same(original, Assert.IsType<EventStreamCommitUnknownException>(error).InnerException);
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
    public async Task ShouldOmitIdentifiersFromMessageGivenConcurrencyFailure(uint code)
    {
        // Arrange
        var original = new StreamException("rejected", "APPEND_FAILED", domainCode: code);
        var admission = code == FitzErrorCodes.StreamSessionAlreadyActive;
        var session = new Session(admission ? "success" : "append", false) { Failure = original };
        var streams = new Streams(session, admission ? original : null);
        var store = new FitzEventStore(streams,
            ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Declined>(1, "Declined")));
        var account = new Account(Uuid.CreateVersion4());

        // Act
        account.Audit(new Declined("pending"));

        // Assert
        var error = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() =>
            new AggregateRepository(store).SaveAsync(account, _saveContext).AsTask());

        var stream = EventStreamAddress.Parse(streams.Route!);
        Assert.Contains(stream.Area, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stream.Realm, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stream.Resource, error.Message, StringComparison.Ordinal);
    }

    static MeterListener ListenToCleanupFaults(out List<KeyValuePair<string, object?>[]> faults)
    {
        var captured = new List<KeyValuePair<string, object?>[]>();
        faults = captured;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                    instrument.Name == "portia.worker.failure")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            if (values.Any(tag => tag.Key == "portia.runner.name" &&
                                  Equals(tag.Value, nameof(FitzEventStore))) &&
                values.Any(tag => tag.Key == "portia.stage" && Equals(tag.Value, "cleanup")))
            {
                captured.Add(values);
            }
        });
        listener.Start();
        return listener;
    }

    sealed class Session(string failureAt, bool cleanupFails) : IStreamSession
    {
        public Exception Failure { get; init; } = new IOException("Injected session failure");

        public int Rollbacks { get; private set; }

        public bool Disposed { get; private set; }

        public bool Committed { get; private set; }

        public List<(ulong Offset, ReadOnlyMemory<byte> Body, ReadOnlyMemory<byte>? Metadata)> Appended { get; } = [];

        public Task<ulong?> AppendAsync(ulong expectedOffset, ReadOnlyMemory<byte> body,
            ReadOnlyMemory<byte>? metadata = null, string? discriminator = null, CancellationToken ct = default)
        {
            if (failureAt == "append")
                throw Failure;
            if (Committed)
                throw new StreamException("offset occupied", "APPEND_FAILED", domainCode: FitzErrorCodes.StreamConcurrencyConflict);
            Appended.Add((expectedOffset, body, metadata));
            return Task.FromResult<ulong?>(expectedOffset);
        }

        public Task CommitAsync(StreamCommitMode mode, CancellationToken ct = default)
        {
            Assert.Equal(StreamCommitMode.Sync, mode);
            if (failureAt == "commit")
                throw Failure;

            Committed = true;
            if (failureAt == "lost-reply")
                throw Failure;
            return Task.CompletedTask;
        }

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

        public bool ReadFails { get; set; }

        public bool TruncateRead { get; init; }

        public List<ulong> SuccessfulReads { get; } = [];

        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default)
        {
            Route = route;
            if (beginFailure is not null)
                return Task.FromException<IStreamSession>(beginFailure);
            return Task.FromResult(session);
        }

        public IAsyncEnumerable<StreamReadBatch> ReadAsync(string route, ulong startOffset, StreamReadMode mode,
            StreamReadOptions? options = null, CancellationToken ct = default)
        {
            if (ReadFails)
                throw new IOException("read unavailable");
            var appended = (Session)session;
            var items = (appended.Committed ? appended.Appended : [])
                .Where(record => record.Offset >= startOffset)
                .Take(TruncateRead ? 1 : checked((int)options!.BatchSize))
                .Select(record => new StreamReadItem(route, StreamReadItemKind.Event,
                    new StreamRecord(route, record.Offset, 0, 0, null, record.Body, record.Metadata, 0)))
                .ToArray();
            SuccessfulReads.Add(startOffset);
            return new[] { new StreamReadBatch(items, startOffset, startOffset + (ulong)items.Length, true) }.ToAsyncEnumerable();
        }

        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
