using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Cntryl.Portia.Communications.Aws;
using Cntryl.Portia.Communications.Azure;
using Cntryl.Portia.Communications.Twilio;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks each provider's send through its real client against Sqrzl's provider HTTP surfaces.</summary>
[Trait("Category", "StorageIntegration")]
public sealed class CommunicationsIntegrationTests
{
    static readonly TenantId Tenant = new("conformance-a");

    /// <summary>Sqrzl's SES v2 surface accepts the request the SES sender builds.</summary>
    [Fact(Skip = "Sqrzl does not yet accept SES message tags or configuration sets (sqrzl-emulator#88).")]
    public async Task ShouldAcceptMailGivenSqrzlSesServer()
    {
        // Arrange
        using var client = new AmazonSimpleEmailServiceV2Client(
            new BasicAWSCredentials("portia-test", "portia-local-secret"),
            new AmazonSimpleEmailServiceV2Config
            {
                ServiceURL = Endpoint(),
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 0,
            });
        var sender = new AwsMailSender(client, new AwsResolver());

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId($"ses-{Guid.NewGuid():N}"), Mail());

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.False(string.IsNullOrEmpty(outcome.ProviderReference));
    }

    /// <summary>Sqrzl's Azure Communication Services email surface accepts the request the Azure sender builds.</summary>
    [Fact]
    public async Task ShouldAcceptMailGivenSqrzlAcsServer()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAzureMailTenantResolver, AzureResolver>();
        _ = services.AddAzureMail(options => options.UseConnectionString(
            $"endpoint={Endpoint()};accesskey={Convert.ToBase64String("portia-local-secret"u8)}"));
        using var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<IMailSender>();

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId($"acs-{Guid.NewGuid():N}"), Mail());

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.False(string.IsNullOrEmpty(outcome.ProviderReference));
    }

    /// <summary>Sqrzl's Twilio Messages surface accepts the request the Twilio sender builds.</summary>
    [Fact]
    public async Task ShouldAcceptSmsGivenSqrzlTwilioServer()
    {
        // Arrange
        using var http = new HttpClient { BaseAddress = new Uri(Endpoint().TrimEnd('/') + "/") };
        var sender = new TwilioSmsSender(http, new TwilioResolver(), null);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId($"sms-{Guid.NewGuid():N}"),
            new SmsMessage("+15005550006", "Portia integration"));

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.StartsWith("SM", outcome.ProviderReference, StringComparison.Ordinal);
    }

    static string Endpoint() => Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT")
        ?? throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'.");

    static MailMessage Mail() => new(["person@example.com"], "Portia integration", "Plain text")
    {
        Html = "<p>Html</p>",
        ReplyTo = "reply@example.com",
    };

    sealed class AwsResolver : IAwsMailTenantResolver
    {
        public ValueTask<AwsMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new AwsMailIdentity("sender@example.com", "portia-events"));
    }

    sealed class AzureResolver : IAzureMailTenantResolver
    {
        public ValueTask<AzureMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new AzureMailIdentity("sender@example.com"));
    }

    sealed class TwilioResolver : ITwilioTenantResolver
    {
        // The emulator requires a correctly shaped SID; generate a fresh synthetic account for each fixture.
        readonly string _accountSid = $"AC{Guid.NewGuid():N}";

        public ValueTask<TwilioTenant> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new TwilioTenant(_accountSid, "SKtestkey",
                "secret", "+15005550001"));
    }
}
