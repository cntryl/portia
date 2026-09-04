using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// An <see cref="IRequestActorValidator" /> for tests that don't need real JWT verification: any
/// non-null token succeeds as <see cref="RequestActor.System" /> except <paramref name="rejectToken" />,
/// which fails as if it had expired — enough to exercise a transport's actor re-validation path
/// (queue, notifications, fleet) without a real signing key. A <see langword="null" /> token
/// always fails, matching a real <see cref="IRequestActorValidator" /> implementation's behavior
/// for a missing token — this fake must honor the same "missing token fails" contract the
/// interface documents, or a test written against it could never exercise a broken "no token"
/// path.
/// </summary>
/// <param name="rejectToken">
/// The one non-null token this validator treats as invalid, or <see langword="null" /> to accept
/// every non-null token.
/// </param>
public sealed class TestRequestActorValidator(string? rejectToken = null) : IRequestActorValidator
{
    /// <inheritdoc />
    public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
        ValueTask.FromResult(token is null || token == rejectToken
            ? Result<ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized, token is null ? "No actor token was provided." : "Token expired."))
            : Result<ClaimsPrincipal>.Success(RequestActor.System));
}
