using System.Security.Claims;

namespace Cntryl.Portia.Testing;

/// <summary>
///     An <see cref="IPermissionEvaluator" /> that either grants or denies every permission it's
///     asked about, for tests that need a working evaluator without a real policy store. Records
///     every permission it was asked to evaluate, so a test can assert on exactly what was checked.
/// </summary>
/// <param name="grantsEveryPermission">
///     Whether every <see cref="EvaluateAsync" /> call should succeed. Use
///     <see cref="AllowAll" />/<see cref="DenyAll" /> instead of this constructor directly.
/// </param>
public sealed class TestPermissionEvaluator(bool grantsEveryPermission) : IPermissionEvaluator
{
    readonly List<string> _evaluated = [];
    readonly Lock _gate = new();

    /// <summary>
    ///     Gets a snapshot of every permission this evaluator has been asked to evaluate, in order.
    /// </summary>
    public IReadOnlyList<string> EvaluatedPermissions
    {
        get
        {
            lock (_gate)
                return [.. _evaluated];
        }
    }

    /// <inheritdoc />
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        lock (_gate)
            _evaluated.Add(permission);

        return ValueTask.FromResult(grantsEveryPermission
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, $"Missing permission '{permission}'.")));
    }

    /// <summary>
    ///     Creates an evaluator that grants every permission it's asked about.
    /// </summary>
    /// <returns>An evaluator whose every evaluation succeeds.</returns>
    public static TestPermissionEvaluator AllowAll() => new(true);

    /// <summary>
    ///     Creates an evaluator that denies every permission it's asked about.
    /// </summary>
    /// <returns>An evaluator whose every evaluation fails with <see cref="RequestErrorKind.Forbidden" />.</returns>
    public static TestPermissionEvaluator DenyAll() => new(false);
}
