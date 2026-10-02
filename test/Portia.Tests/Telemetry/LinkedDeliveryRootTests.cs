using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Cntryl.Portia;

/// <summary>Linked deliveries are roots even when received under an unrelated ambient activity.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class LinkedDeliveryRootTests
{
    const string Parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    /// <summary>Wire ingress uses remote-parent sampling while an adapter's SDK parent stays local.</summary>
    [Fact]
    public void ShouldSampleWireParentsAsRemoteAndSdkParentsAsLocal()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        _ = services.AddOpenTelemetry().WithPortia().WithTracing(tracing => tracing
            .SetSampler(new ParentBasedSampler(new AlwaysOnSampler(),
                remoteParentSampled: new AlwaysOnSampler(), remoteParentNotSampled: new AlwaysOnSampler(),
                localParentSampled: new AlwaysOffSampler(), localParentNotSampled: new AlwaysOffSampler()))
            .AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        var propagated = new RequestTraceContext(Parent);
        using (var received = PortiaTelemetry.StartProcess("remote.rpc", new RpcInvocation("rpc://test"), propagated))
            Assert.True(received?.Recorded);
        using (var local = PortiaTelemetry.StartProcess("local.sdk", new RpcInvocation("rpc://test"), propagated,
                   receivedTraceContext: false))
            Assert.False(local?.Recorded == true);
        Assert.True(tracer.ForceFlush());
        Assert.Equal("remote.rpc", Assert.Single(exported).GetTagItem("portia.request.name"));
    }

    /// <summary>A linked delivery uses root sampling independently of an unrelated sampled caller.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldUseRootSamplingForLinkedDelivery(bool sampleRoot)
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry().WithPortia().WithTracing(tracing => tracing
            .SetSampler(new ParentBasedSampler(sampleRoot ? new AlwaysOnSampler() : new AlwaysOffSampler()))
            .AddInMemoryExporter(exported));
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        using var caller = new Activity("sampled-unrelated-caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        caller.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        using (var process = PortiaTelemetry.StartProcess("linked.sampling", new QueueInvocation("queue://test", 1),
                   new RequestTraceContext(Parent)))
        {
            Assert.Equal(sampleRoot, process?.Recorded == true);
            if (process is not null)
                Assert.Equal(default, process.ParentSpanId);
        }
        Assert.Same(caller, Activity.Current);
        Assert.True(tracer.ForceFlush());
        Assert.Equal(sampleRoot ? 1 : 0, exported.Count);
    }

    /// <summary>Retries and firings preserve a producer link and restore ambient state through asynchronous scopes.</summary>
    [Theory]
    [InlineData("queue", "valid")]
    [InlineData("queue", "absent")]
    [InlineData("queue", "invalid")]
    [InlineData("notice", "valid")]
    [InlineData("notice", "absent")]
    [InlineData("notice", "invalid")]
    [InlineData("schedule", "valid")]
    [InlineData("schedule", "absent")]
    [InlineData("schedule", "invalid")]
    public async Task ShouldCreateIndependentLinkedRootsAndRestoreAmbient(string transport, string propagation)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("unrelated").SetIdFormat(ActivityIdFormat.W3C).Start();
        RequestInvocation invocation = transport switch
        {
            "queue" => new QueueInvocation("queue://tests", 2),
            "notice" => new NoticeInvocation("notice://tests"),
            _ => new ScheduleInvocation("schedule://tests")
        };
        var context = propagation switch
        {
            "valid" => new RequestTraceContext(Parent),
            "invalid" => new RequestTraceContext("invalid-secret-trace-field"),
            _ => null
        };
        var traces = new HashSet<ActivityTraceId>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using (var process = PortiaTelemetry.StartProcess("linked.delivery", invocation, context))
            {
                Assert.NotNull(process);
                Assert.NotEqual(ambient.TraceId, process.TraceId);
                Assert.Equal(default, process.ParentSpanId);
                Assert.Null(process.Parent);
                Assert.True(traces.Add(process.TraceId));
                if (propagation == "valid")
                {
                    var link = Assert.Single(process.Links).Context;
                    Assert.Equal(Parent[3..35], link.TraceId.ToString());
                    Assert.True(link.IsRemote);
                }
                else
                    Assert.Empty(process.Links);
                await Task.Yield();
                Assert.Same(process, Activity.Current);
                using (var execute = PortiaTelemetry.StartExecute("linked.delivery", transport))
                {
                    Assert.NotNull(execute);
                    Assert.Equal(process.SpanId, execute.ParentSpanId);
                    await Task.Yield();
                    Assert.Same(execute, Activity.Current);
                }
                Assert.Same(process, Activity.Current);
            }
            Assert.Same(ambient, Activity.Current);
        }
    }
}
