using System.Security.Claims;
using System.Text.Encodings.Web;
using Cntryl.Portia;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Portia.Accounts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAccountsApplication(builder.Configuration, builder.Environment);
builder.Services.AddAccountsTelemetry("portia.accounts.api",
    tracing => tracing.AddAspNetCoreInstrumentation());
var useDemoAuthentication = builder.Environment.IsDevelopment();
var authentication = builder.Services.AddAuthentication(options =>
{
    if (useDemoAuthentication)
        options.DefaultScheme = AccountDemoAuthenticationHandler.SchemeName;
});
if (useDemoAuthentication)
    authentication.AddScheme<AuthenticationSchemeOptions, AccountDemoAuthenticationHandler>(
        AccountDemoAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
_ = builder.Services.AddPortia().AddHttp();

await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapPortiaPost<OpenAccount>("/tenants/{tenant}/accounts/{account_id}/open");
app.MapPortiaPost<DepositFunds>("/tenants/{tenant}/accounts/{account_id}/deposits");
app.MapPortiaGet<GetAccount, AccountReadModel>("/tenants/{tenant}/accounts/{account_id}");
app.MapPortiaOpenApi();
await app.RunAsync();

/// <summary>Development-only header authentication for the self-contained sample.</summary>
sealed class AccountDemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AccountsDemo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Demo-Tenant", out var tenant))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!Request.Headers.TryGetValue("X-Demo-Account", out var accountId))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (tenant.Count != 1 || string.IsNullOrWhiteSpace(tenant[0]))
            return Task.FromResult(AuthenticateResult.Fail("X-Demo-Tenant must contain one tenant ID."));
        if (accountId.Count != 1 || string.IsNullOrWhiteSpace(accountId[0]))
            return Task.FromResult(AuthenticateResult.Fail("X-Demo-Account must contain one account ID."));

        var identity = new ClaimsIdentity(
            [new Claim("tenant_id", tenant[0]!),
                new Claim("account_id", accountId[0]!),
                new Claim("sub", "accounts-demo-user", ClaimValueTypes.String, "https://accounts.example.test")],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
