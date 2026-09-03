using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// An <see cref="IPermissionEvaluator" /> that either grants or denies every permission it's
/// asked about, for tests that need a working evaluator without a real policy store. Records
/// every permission it was asked to evaluate, so a test can assert on exactly what was checked.
/// </summary>
/// <param name="grantsEveryPermission">
/// Whether every <see cref="EvaluateAsync" /> call should succeed. Use
/// <see cref="AllowAll" />/<see cref="DenyAll" /> instead of this constructor directly.
/// </param>
public sealed class TestPermissionEvaluator(bool grantsEveryPermission) : IPermissionEvaluator
{
    /// <summary>
    /// Gets every permission this evaluator has been asked to evaluate, in order.
    /// </summary>
    public List<string> EvaluatedPermissions { get; } = [];

    /// <summary>
    /// Creates an evaluator that grants every permission it's asked about.
    /// </summary>
    public static TestPermissionEvaluator AllowAll() => new(grantsEveryPermission: true);

    /// <summary>
    /// Creates an evaluator that denies every permission it's asked about.
    /// </summary>
    public static TestPermissionEvaluator DenyAll() => new(grantsEveryPermission: false);

    /// <inheritdoc />
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        EvaluatedPermissions.Add(permission);

        return ValueTask.FromResult(grantsEveryPermission
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, $"Missing permission '{permission}'.")));
    }
}
