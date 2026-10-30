using System.Globalization;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Qualifies the public projection contract through real Fitz and Cassie servers.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "CassieIntegration")]
public sealed partial class CassieProjectionIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Application data and checkpoint operations satisfy the shared backend contract.</summary>
    [Fact]
    public async Task ShouldSatisfyProjectionConformanceAgainstCassie()
    {
        await using var database = await Database.OpenAsync();
        await ProjectionStoreConformance.VerifyAsync(new Probe(database));
    }

    /// <summary>Real events project to isolated account data and resume without applying increments twice.</summary>
    [Fact]
    public async Task ShouldProjectFitzEventsIntoCassieAndResumeAcrossRepositoryScopes()
    {
        await using var database = await Database.OpenAsync();
        await using var client = await broker.CreateClientAsync();
        var events = new FitzEventStore(client.Stream, TestJson.DomainSerializer(new DomainEventTypeCatalog()
            .Register<ValueChanged>(1, "test.value.changed").Register<ValueIncremented>(1, "test.value.incremented")));
        var realm = "cassie-" + Uuid.CreateVersion4();
        var otherRealm = "cassie-other-" + Uuid.CreateVersion4();
        var account = Uuid.CreateVersion4();
        var identity = Identity(realm);
        DomainEvent[] changes = [new ValueChanged(500), new ValueIncremented(125)];
        for (var i = 0; i < changes.Length; i++)
            changes[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), account, (ulong)i + 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress(realm, "accounts", account.ToString()), 0, changes);

        var expectedFailure = RequestError.Conflict("temporary projection dependency", isTransient: true);
        await using (var repository = new AccountRepository(database))
        {
            var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => new ProjectorRunner(events)
                .RunAsync(new AccountProjector(repository, identity.Pattern, new FailurePlan(expectedFailure)), ProjectionCheckpoint.Start).AsTask());
            Assert.Same(expectedFailure, failure.Error);
            Assert.Equal(ProjectionCheckpoint.Start, await repository.LoadCheckpointAsync(identity));
            Assert.Null(await repository.GetAsync(identity, account.ToString()));
        }

        ProjectionCheckpoint committed;
        await using (var repository = new AccountRepository(database))
        {
            committed = await new ProjectorRunner(events).RunAsync(new AccountProjector(repository, identity.Pattern),
                await repository.LoadCheckpointAsync(identity));
            Assert.Equal(new AccountReadModel(account.ToString(), 625), await repository.GetAsync(identity, account.ToString()));
        }
        await using (var repository = new AccountRepository(database))
        {
            Assert.Equal(committed, await repository.LoadCheckpointAsync(identity));
            var resumed = await new ProjectorRunner(events).RunAsync(new AccountProjector(repository, identity.Pattern),
                await repository.LoadCheckpointAsync(identity));
            Assert.Equal(committed, resumed);
            Assert.Equal(new AccountReadModel(account.ToString(), 625), await repository.GetAsync(identity, account.ToString()));
            Assert.Null(await repository.GetAsync(Identity(otherRealm), account.ToString()));

            var candidate = new CheckpointIdentity(identity.ComponentName, identity.Pattern, "candidate");
            _ = await new ProjectorRunner(events).RunAsync(new AccountProjector(repository, identity.Pattern),
                await repository.LoadCheckpointAsync(candidate), new ProjectionRunOptions { RebuildId = "candidate" });
            Assert.Equal(new AccountReadModel(account.ToString(), 625), await repository.GetAsync(candidate, account.ToString()));
            Assert.Equal(committed, await repository.LoadCheckpointAsync(identity));
        }
    }

    /// <summary>Hosted retry and permanent-failure decisions preserve real model data and progress.</summary>
    /// <param name="transient">Whether the first returned failure permits a retry.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldApplyFrameworkRetryPolicyAcrossFitzAndCassie(bool transient)
    {
        await using var database = await Database.OpenAsync();
        await using var client = await broker.CreateClientAsync();
        var events = new FitzEventStore(client.Stream, TestJson.DomainSerializer(new DomainEventTypeCatalog()
            .Register<ValueChanged>(1, "test.value.changed").Register<ValueIncremented>(1, "test.value.incremented")));
        var identity = Identity("cassie-hosted-" + Uuid.CreateVersion4());
        var account = Uuid.CreateVersion4();
        DomainEvent[] changes = [new ValueChanged(500), new ValueIncremented(125)];
        for (var i = 0; i < changes.Length; i++)
            changes[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), account, (ulong)i + 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress(identity.Pattern.Realm, "accounts", account.ToString()), 0, changes);
        var error = RequestError.Conflict("injected handler failure", transient);
        var failures = new FailurePlan(error);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventReader>(events);
        services.AddScoped(sp => new AccountRepository(sp.GetRequiredService<CassieProjectionDatabase>(), database.Accounts,
            () => committed.TrySetResult()));
        services.AddScoped(sp => new AccountProjector(sp.GetRequiredService<AccountRepository>(), identity.Pattern, failures));
        services.AddPortia().AddCassie(database.Source.ConnectionString, database.Checkpoints)
            .AddCassieProjection<AccountProjector, AccountRepository>(identity.ComponentName, WorkloadScope.Global, options =>
        {
            options.PollInterval = TimeSpan.FromHours(1);
            options.MaximumFailureDelay = TimeSpan.FromMilliseconds(20);
            options.FailureAttemptLimit = 3;
        }).AddWorkers().UseSingleProcessWorkloads();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            if (transient)
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await worker.StopAsync(default);
                Assert.Equal(2, failures.Attempts);
            }
            else
            {
                var terminal = await Assert.ThrowsAsync<WorkloadFailureException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal(1, terminal.Attempts);
                var diagnostic = Assert.IsType<ProcessorFailureException>(terminal.InnerException);
                Assert.Same(error, diagnostic.Error);
                Assert.Equal(ProcessorFailureStage.Handling, diagnostic.Stage);
                Assert.Equal(1, failures.Attempts);
            }

            await using var queryScope = provider.CreateAsyncScope();
            var repository = queryScope.ServiceProvider.GetRequiredService<AccountRepository>();
            var checkpoint = await repository.LoadCheckpointAsync(identity);
            if (transient)
            {
                Assert.NotEqual(ProjectionCheckpoint.Start, checkpoint);
                Assert.Equal(new AccountReadModel(account.ToString(), 625), await repository.GetAsync(identity, account.ToString()));
            }
            else
            {
                Assert.Equal(ProjectionCheckpoint.Start, checkpoint);
                Assert.Null(await repository.GetAsync(identity, account.ToString()));
            }
        }
        finally
        {
            if (worker.ExecuteTask is { IsCompleted: false })
                await worker.StopAsync(default);
            worker.Dispose();
        }
    }

    internal sealed class FailurePlan(RequestError error)
    {
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public RequestError? TakeError() => Interlocked.Increment(ref _attempts) == 1 ? error : null;
    }

    static CheckpointIdentity Identity(string realm) => new("AccountProjector", EventStreamPattern.ForPattern(realm, "accounts"));

    internal sealed record AccountReadModel(string AccountId, long BalanceCents);

    internal sealed partial class AccountProjector(AccountRepository accounts, EventStreamPattern pattern, FailurePlan? failures = null)
        : BatchProjector(accounts, pattern, "AccountProjector"), IProjectorHandler<ValueChanged>, IProjectorHandler<ValueIncremented>
    {
        public async ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct)
        {
            await accounts.AddAsync(context.Trigger.Metadata.AggregateId.ToString(), context.Trigger.Value, ct);
            return Result.Success;
        }
        public async ValueTask<Result> HandleAsync(IProjectorContext<ValueIncremented> context, CancellationToken ct)
        {
            await accounts.AddAsync(context.Trigger.Metadata.AggregateId.ToString(), context.Trigger.Amount, ct);
            var failure = failures?.TakeError();
            return failure is null ? Result.Success : Result.Failure(failure);
        }
    }

    internal sealed class Database(CassieDataSource source, string prefix) : IAsyncDisposable
    {
        public CassieDataSource Source { get; } = source;
        public string Checkpoints { get; } = prefix + "_checkpoints";
        public string Accounts { get; } = prefix + "_accounts";
        public CassieProjectionDatabase Persistence { get; } = new(source, prefix + "_checkpoints");
        public static async Task<Database> OpenAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("PORTIA_CASSIE_TEST_CONNECTION_STRING")
                ?? throw new InvalidOperationException("Start Compose's cassie and fitz services and set PORTIA_CASSIE_TEST_CONNECTION_STRING.");
            var database = new Database(new CassieDataSource(connectionString), "portia_" + Guid.NewGuid().ToString("N"));
            await using var connection = await database.Source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            await database.Persistence.InitializeAsync();
            command.CommandText = $"CREATE TABLE {database.Accounts} (identity TEXT PRIMARY KEY, balance BIGINT NOT NULL, label TEXT NOT NULL)";
            _ = await command.ExecuteNonQueryAsync();
            return database;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = await Source.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP TABLE {Accounts}";
                _ = await command.ExecuteNonQueryAsync();
                command.CommandText = $"DROP TABLE {Checkpoints}";
                _ = await command.ExecuteNonQueryAsync();
            }
            finally
            {
                Persistence.Dispose();
                await Source.DisposeAsync();
            }
        }
    }

    // Native application schema and operations; the library owns transaction and checkpoint plumbing.
    internal sealed class AccountRepository(CassieProjectionDatabase persistence, string accountsTable, Action? onCommitted = null)
        : CassieProjectionRepository(persistence, "AccountProjector"),
            IProjectionStoreConformanceSession, IProjectionStore
    {
        public AccountRepository(Database database, Action? onCommitted = null)
            : this(database.Persistence, database.Accounts, onCommitted) { }

        bool _failCommit;
        readonly Action? _onCommitted = onCommitted;
        public IProjectionStore Store => this;

        async ValueTask<IProjectionBatch> IProjectionStore.BeginAsync(ProjectionBatchContext context, CancellationToken ct)
            => new ObservedBatch(await base.BeginAsync(context, ct), this);

        public async ValueTask AddAsync(string accountId, long amount, CancellationToken ct)
        {
            await using var command = CreateCommand($"INSERT INTO {accountsTable} (identity, balance, label) VALUES (@identity, @amount, '') ON CONFLICT (identity) DO UPDATE SET balance = {accountsTable}.balance + EXCLUDED.balance");
            Bind(command, ("identity", CurrentProjectionKey + accountId), ("amount", amount));
            _ = await command.ExecuteNonQueryAsync(ct);
        }
        public async ValueTask<AccountReadModel?> GetAsync(CheckpointIdentity identity, string accountId, CancellationToken ct = default)
        {
            await using var connection = await OpenReadAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT balance FROM {accountsTable} WHERE identity = @identity";
            Bind(command, ("identity", GetProjectionKey(identity) + accountId));
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : new AccountReadModel(accountId, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        public async ValueTask StageValueAsync(string value, CancellationToken ct = default)
        {
            await using var command = CreateCommand($"INSERT INTO {accountsTable} (identity, balance, label) VALUES (@identity, 0, @label) ON CONFLICT (identity) DO UPDATE SET label = EXCLUDED.label");
            Bind(command, ("identity", CurrentProjectionKey + "probe"), ("label", value));
            _ = await command.ExecuteNonQueryAsync(ct);
        }
        public async ValueTask<string?> ReadValueAsync(CheckpointIdentity identity, CancellationToken ct = default)
        {
            await using var connection = await OpenReadAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT label FROM {accountsTable} WHERE identity = @identity";
            Bind(command, ("identity", GetProjectionKey(identity) + "probe"));
            return await command.ExecuteScalarAsync(ct) as string;
        }
        public ValueTask FailNextCommitAsync(CancellationToken ct = default) { _failCommit = true; return ValueTask.CompletedTask; }
        static void Bind(System.Data.Common.DbCommand command, params (string Name, object Value)[] parameters)
        {
            foreach (var parameter in parameters)
                _ = command.Parameters.Add(new CassieParameter { ParameterName = parameter.Name, Value = parameter.Value });
        }
        sealed class ObservedBatch(IProjectionBatch batch, AccountRepository repository) : IProjectionBatch
        {
            public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
            {
                if (repository._failCommit)
                    throw new IOException("Injected before commit.");
                await batch.CommitAsync(checkpoint, ct);
                repository._onCommitted?.Invoke();
            }
            public ValueTask DisposeAsync() => batch.DisposeAsync();
        }
    }

    sealed class Probe(Database database) : IProjectionStoreConformanceProbe
    {
        public CheckpointIdentity LiveIdentity { get; } = Identity("conformance");
        public CheckpointIdentity RebuildIdentity { get; } = new("AccountProjector", EventStreamPattern.ForPattern("conformance", "accounts"), "candidate");
        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IProjectionStoreConformanceSession> OpenSessionAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IProjectionStoreConformanceSession>(new AccountRepository(database));
    }
}
