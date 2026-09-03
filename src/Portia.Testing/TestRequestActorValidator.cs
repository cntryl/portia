using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// An <see cref="IRequestActorValidator" /> for tests that don't need real JWT verification: any
/// token succeeds as <see cref="RequestActor.System" /> except <paramref name="rejectToken" />,
/// which fails as if it had expired — enough to exercise a transport's actor re-validation path
/// (queue, live delivery, fleet) without a real signing key.
/// </summary>
/// <param name="rejectToken">
/// The one token this validator treats as invalid, or <see langword="null" /> to accept every
/// token.
/// </param>
public sealed class TestRequestActorValidator(string? rejectToken = null) : IRequestActorValidator
{
    /// <inheritdoc />
    public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
        ValueTask.FromResult(token is not null && token == rejectToken
            ? Result<ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized, "Token expired."))
            : Result<ClaimsPrincipal>.Success(RequestActor.System));
}
