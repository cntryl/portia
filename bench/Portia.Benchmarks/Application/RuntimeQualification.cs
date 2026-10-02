namespace Cntryl.Portia;

static class RuntimeQualification
{
    internal static async Task RunAsync()
    {
        if (System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
            throw new InvalidOperationException("Runtime qualification requires JSON reflection to be disabled.");
        using (var application = new RuntimeApplication())
            await application.QualifyAsync();
        using (var application = new RuntimeApplication(yield: true))
            await application.QualifyAsync();
        foreach (var cohort in new[] { "none", "propagation", "sampled", "metrics", "logs-disabled", "logs-enabled" })
        {
            using var benchmark = new RequestLifecycleTelemetryBenchmarks { Cohort = cohort };
            benchmark.Setup();
            if (!(await benchmark.Command()).IsSuccess)
                throw new InvalidOperationException("Telemetry cohort command failed.");
        }
        foreach (var yielding in new[] { false, true })
        {
            using var application = new AggregateApplication(yielding);
            await application.QualifyAsync();
        }
        foreach (var size in new[] { 256, 4096, 65536 })
            foreach (var text in new[] { "ascii", "escaped" })
            {
                var benchmark = new EnvelopeLifecycleBenchmarks { PayloadLength = size, Text = text };
                benchmark.Setup();
            }
        foreach (var transport in new[] { "inprocess", "loopback" })
            foreach (var size in new[] { 256, 4096, 65536 })
            {
                using var application = new HttpLifecycleApplication(transport, size);
                await application.QualifyAsync();
            }
        foreach (var count in new[] { 0, 33, 4097 })
            foreach (var batch in new[] { 1, 32, 128 })
                foreach (var yielding in new[] { false, true })
                {
                    using var application = new MixedProcessorApplication(count, batch, yielding);
                    await application.QualifyAsync();
                }
        foreach (var count in new[] { 33, 4097 })
            foreach (var batch in new[] { 1, 128 })
                foreach (var boundary in new[] { "sync-checkpoint", "async-checkpoint", "awaited-effect" })
                    await new MixedReactorApplication(count, batch, boundary).QualifyAsync();
        Console.WriteLine("Qualified request, aggregate, envelope, generated HTTP and mixed processor matrices.");
    }
}
