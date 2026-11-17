using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

enum AuditLargeMode : long { Rounded = 9_007_199_254_740_993L }
sealed record InvalidDefaultRequest([property: Range(1, 10)] int Limit = 0) : IRequest<string>, ICallable;
sealed record ConfirmAudit([property: Range(1, 1)] bool Accepted, bool Ordinary, string Marker = "ok") : IRequest<string>, ICallable;
sealed record RoundTripAudit(AuditReply Payload) : IRequest<AuditReply>, ICallable;
sealed record ReadAudit : IRequest<AuditReply>, ICallable;
sealed record AuditReply([property: StringLength(20)] string LongPropertyName);
sealed class AuditHandler : IRequestHandler<InvalidDefaultRequest, string>, IRequestHandler<ReadAudit, AuditReply>, IRequestHandler<RoundTripAudit, AuditReply>, IRequestHandler<ConfirmAudit, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ConfirmAudit> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success("accepted"));
    public ValueTask<Result<string>> HandleAsync(IRequestContext<InvalidDefaultRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success("accepted"));
    public ValueTask<Result<AuditReply>> HandleAsync(IRequestContext<RoundTripAudit> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<AuditReply>.Success(context.Request.Payload));
    public ValueTask<Result<AuditReply>> HandleAsync(IRequestContext<ReadAudit> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<AuditReply>.Success(new("accepted")));
}
sealed class AuditAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
        Request.Headers.ContainsKey("X-" + Scheme.Name)
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "audit")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
}
