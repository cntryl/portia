namespace Cntryl.Portia;

/// <summary>Verifies <see cref="TestPermissionEvaluator" /> records every evaluation, including concurrent ones.</summary>
public sealed class TestPermissionEvaluatorTests
{
    /// <summary>Concurrent dispatches sharing one singleton evaluator lose no recorded permission.</summary>
    [Fact]
    public async Task ShouldRecordEveryConcurrentEvaluation()
    {
        var evaluator = TestPermissionEvaluator.AllowAll();

        await Parallel.ForAsync(0, 10_000, async (index, ct) =>
            _ = await evaluator.EvaluateAsync(RequestActor.System, $"p{index}", ct));

        Assert.Equal(10_000, evaluator.EvaluatedPermissions.Distinct().Count());
    }
}
