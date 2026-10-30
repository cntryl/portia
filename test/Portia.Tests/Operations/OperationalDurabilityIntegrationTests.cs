using System.Diagnostics;
using System.Net;

namespace Cntryl.Portia;

/// <summary>Tests real process termination and broker recreation using an isolated persistent volume.</summary>
[Trait("Category", "DurabilityIntegration")]
public sealed class OperationalDurabilityIntegrationTests : IAsyncLifetime
{
    const string Image = "ghcr.io/cntryl/fitz@sha256:6d62d9ed9d9fd32186391e6f5944a05a5894e7bd2c7e117288c5ce87acb3035c";
    const string Route = "kv://qualification/portia/checkpoints";
    const int StorageLeaseSeconds = 30;
    static readonly OperationalTarget Target = new("durability-reactor", "stream://portia-durability/source/*", OperationalScope.Global);
    readonly string _name = "portia-operations-test-" + Guid.NewGuid().ToString("N");
    readonly string _directory = Path.Combine(Path.GetTempPath(), "portia-operations-test-" + Guid.NewGuid().ToString("N"));
    readonly List<Process> _children = [];
    Uri _endpoint = null!;

    /// <summary>Creates only this test's volume and broker.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _ = await DockerAsync("volume", "create", _name);
        await StartBrokerAsync();
    }

    /// <summary>Releases this test's processes, broker, and volume even after an assertion fails.</summary>
    public async Task DisposeAsync()
    {
        foreach (var child in _children)
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            child.Dispose();
        }
        _ = await DockerAsync("rm", "--force", "--volumes", _name);
        _ = await DockerAsync("volume", "rm", _name);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Failure and interrupted-attempt evidence survive hard termination; replay does not duplicate an earlier effect.</summary>
    [Fact]
    public async Task ShouldRecoverAfterWorkerKillAndBrokerRecreationWithoutDuplicatingEffects()
    {
        await using (var client = await ConnectAsync())
        {
            var events = Events(client);
            var aggregate = Uuid.CreateVersion4();
            DomainEvent[] source = [new ValueChanged(1), new ValueChanged(2)];
            for (var i = 0; i < source.Length; i++)
                source[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, (ulong)i + 1, DateTimeOffset.UtcNow));
            await events.AppendAsync(new EventStreamAddress("portia-durability", "source", aggregate.ToString()), 0, source);
        }
        var failed = await StartWorkerAsync("blocked");
        Guid failedAttempt;
        await using (var client = await ConnectAsync())
        {
            var state = (await new FitzOperationalStore(client.Kv, Route).ReadAsync(Target))!;
            failedAttempt = Assert.Single(state.Attempts).Id;
            Assert.NotNull(state.Block);
            Assert.Equal(2, state.Attempts[0].Sources.Count);
            Assert.Single(await ReadAsync(Events(client), "effects"));
        }
        await KillAsync(failed);
        await RecreateBrokerAsync();
        await using (var client = await ConnectAsync())
        {
            var journal = new FitzOperationalStore(client.Kv, Route);
            var persisted = (await journal.ReadAsync(Target))!;
            Assert.NotNull(persisted.Block);
            Assert.Equal(failedAttempt, Assert.Single(persisted.Attempts).Id);
            Assert.Equal(ProjectionCheckpoint.Start, await journal.LoadAsync(Target.Identity));
            Assert.Single(await ReadAsync(Events(client), "effects"));
        }

        // This process approves and queues the exact persisted plan. It is killed inside the
        // first replayed handler after the durable start and after observing the earlier effect.
        var interrupted = await StartWorkerAsync("crash");
        await KillAsync(interrupted);
        await RecreateBrokerAsync();
        _ = await StartWorkerAsync("recover");
        await using (var client = await ConnectAsync())
        {
            var journal = new FitzOperationalStore(client.Kv, Route);
            var state = (await journal.ReadAsync(Target))!;
            Assert.Null(state.Block);
            Assert.Equal(3, state.Attempts.Count);
            Assert.Equal("failed", state.Attempts.Single(a => a.Id == failedAttempt).Outcome);
            Assert.Single(state.Attempts, a => a.Outcome == "interrupted");
            var committed = Assert.Single(state.Attempts, a => a.Outcome == "committed");
            Assert.Equal(3, state.Attempts.Select(a => a.OwnerSession).Distinct().Count());
            Assert.Equal(state.Attempts[0].Sources, committed.Sources);
            var source = await ReadAsync(Events(client), "source");
            var checkpoint = await journal.LoadAsync(Target.Identity);
            Assert.Equal(source[^1].NextCursor, checkpoint.Cursor);
            var effects = await ReadAsync(Events(client), "effects");
            Assert.Equal(2, effects.Count);
            Assert.Equal(2, effects.Select(e => e.Event.Metadata.EventId).Distinct().Count());
            Assert.Equal([1, 2], effects.Select(e => ((ValueChanged)e.Event).Value));
            var repair = Assert.Single(state.Repairs);
            var api = new PortiaOperations(journal, new OperationalOptions(), new EmptyApplicationTopology(), new TestOperationalAuthorizer());
            Assert.Equal("indeterminate", (await api.VerifyRepairAsync(TestOperationalAuthorizer.Actor(), Target, repair.PlanId)).Status);
            Assert.Empty(await api.ListIssuesAsync(TestOperationalAuthorizer.Actor(), OperationalScope.Global));
        }
    }

    async Task<Process> StartWorkerAsync(string mode)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assembly = Path.GetFullPath($"../../../../../test/Portia.Operations.TestWorker/bin/{configuration}/net10.0/Portia.Operations.TestWorker.dll", AppContext.BaseDirectory);
        var signal = Path.Combine(_directory, mode);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(signal);
        start.Environment["FITZ_TEST_ENDPOINT"] = _endpoint.ToString();
        var child = Process.Start(start)!;
        _children.Add(child);
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!File.Exists(signal))
        {
            if (child.HasExited)
                Assert.Fail($"Worker exited before {mode}: {await output}\n{await error}");
            await Task.Delay(20, deadline.Token);
        }
        return child;
    }

    static async Task KillAsync(Process child)
    {
        Assert.False(child.HasExited);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        Assert.NotEqual(0, child.ExitCode);
    }

    async Task RecreateBrokerAsync()
    {
        _ = await DockerAsync("kill", "--signal", "KILL", _name);
        _ = await DockerAsync("rm", "--volumes", _name);
        await StartBrokerAsync();
    }

    async Task StartBrokerAsync()
    {
        _ = await DockerAsync("run", "--detach", "--name", _name, "--init", "--publish", "127.0.0.1::4090",
            "--mount", $"source={_name},target=/data", "--env", "FITZ_ADMIN_AUTH_MODE=open",
            "--env", "FITZ_AUTH_REQUIRED=false", "--env", "FITZ_HTTP_PORT=4090", "--env", "FITZ_STORAGE_MODE=local",
            "--env", "FITZ_STORAGE_PATH=/data", "--env", $"FITZ_STORAGE_LEASE_TTL_SECS={StorageLeaseSeconds}",
            "--env", "FITZ_TCP_ENABLED=false", Image);
        var binding = (await DockerAsync("port", _name, "4090/tcp")).Trim();
        _endpoint = new Uri($"ws://{IPEndPoint.Parse(binding)}/ws");
        // A killed broker cannot release its persistent writer lease. Observe the real readiness
        // boundary after that configured lease expires; the ordinary client connect budget stays 20s.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(StorageLeaseSeconds + 20));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ready = new UriBuilder(_endpoint) { Scheme = "http", Port = _endpoint.Port, Path = "/readyz" }.Uri;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(ready, deadline.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(100, deadline.Token);
        }
    }

    async Task<Client> ConnectAsync()
    {
        var client = new Client(new ClientConfig(_endpoint));
        try
        {
            await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(20)));
            return client;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException("Qualification broker failed to become ready: "
                + await DockerAsync("inspect", "--format", "{{json .State}}", _name)
                + await DockerAsync("logs", _name), ex);
        }
    }

    static FitzEventStore Events(Client client) => new(client.Stream, TestJson.DomainSerializer(new DomainEventTypeCatalog()
        .Register<ValueChanged>(1, "portia.durability.changed")));

    static async Task<List<DomainEventRecord>> ReadAsync(FitzEventStore events, string area)
    {
        var records = new List<DomainEventRecord>();
        await foreach (var record in events.ReadAsync(EventStreamPattern.ForPattern("portia-durability", area), EventCursor.Start, default))
            records.Add(record);
        return records;
    }

    static async Task<string> DockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, $"Docker {arguments[0]} failed: {await error}");
        var stdout = await output;
        return arguments[0] == "logs" ? stdout + await error : stdout;
    }
}
