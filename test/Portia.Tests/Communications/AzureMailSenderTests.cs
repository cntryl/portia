using Azure;
using Azure.Communication.Email;
using Cntryl.Portia.Communications.Azure;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks Azure Communication Services request construction and outcome reporting without network access.</summary>
public sealed class AzureMailSenderTests
{
    const string Recipient = "person@example.com";
    static readonly TenantId Tenant = new("tenant one");

    /// <summary>One request carries the tenant's sender address and is not awaited to completion.</summary>
    [Fact]
    public async Task ShouldSubmitOneRequestWithoutPollingGivenTenantIdentity()
    {
        // Arrange
        var client = new StubEmailClient();
        var sender = new AzureMailSender(client, new Resolver());
        var message = new MailMessage([Recipient], "Subject", "Text")
        {
            Html = "<p>Html</p>",
            ReplyTo = "reply@example.com",
        };

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("order-1.receipt"), message);

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.Equal("operation-1", outcome.ProviderReference);
        var (wait, sent) = Assert.Single(client.Requests);
        Assert.Equal(WaitUntil.Started, wait);
        Assert.Equal("sender@tenant.example", sent.SenderAddress);
        Assert.Equal([Recipient], sent.Recipients.To.Select(address => address.Address));
        Assert.Equal(["reply@example.com"], sent.ReplyTo.Select(address => address.Address));
        Assert.Equal("Subject", sent.Content.Subject);
        Assert.Equal("Text", sent.Content.PlainText);
        Assert.Equal("<p>Html</p>", sent.Content.Html);
    }

    /// <summary>A definitive refusal is a rejection, not an exception.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    public async Task ShouldReportRejectionGivenDefinitiveRefusal(int status)
    {
        // Arrange
        var client = new StubEmailClient { Failure = new RequestFailedException(status, $"Refused for {Recipient}") };
        var sender = new AzureMailSender(client, new Resolver());

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), Message());

        // Assert
        Assert.Equal(SendStatus.Rejected, outcome.Status);
        Assert.Equal(SendRejectionReason.Other, outcome.RejectionReason);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>HTTP 429 is thrown for the caller to pace; the sender does not retry.</summary>
    [Fact]
    public async Task ShouldThrowThrottledWithoutRetryingGivenTooManyRequests()
    {
        // Arrange
        var client = new StubEmailClient { Failure = new RequestFailedException(429, "slow down") };
        var sender = new AzureMailSender(client, new Resolver());

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(Tenant, new SendId("a"), Message()));

        // Assert
        _ = Assert.IsType<SendThrottledException>(exception);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>A server error, no response, or a timeout leaves the result uncertain, and the call is not repeated.</summary>
    [Theory]
    [InlineData(500)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ShouldReportUnknownWithoutRetryingGivenUncertainProviderCall(int status)
    {
        // Arrange
        var client = new StubEmailClient
        {
            Failure = status < 0 ? new TaskCanceledException("timed out") : new RequestFailedException(status, "oops"),
        };
        var sender = new AzureMailSender(client, new Resolver());

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), Message());

        // Assert
        Assert.Equal(SendStatus.Unknown, outcome.Status);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>Refused credentials or a missing resource throw, and the provider's text is not repeated.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ShouldThrowWithoutProviderTextGivenMisconfiguredSender(int status)
    {
        // Arrange
        var client = new StubEmailClient { Failure = new RequestFailedException(status, $"Refused for {Recipient}") };
        var sender = new AzureMailSender(client, new Resolver());

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(Tenant, new SendId("a"), Message()));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.DoesNotContain(Recipient, failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A connection that was never established certainly sent nothing, so it stays a retryable failure.</summary>
    [Fact]
    public async Task ShouldRethrowGivenConnectionNeverEstablished()
    {
        // Arrange
        var client = new StubEmailClient
        {
            Failure = new RequestFailedException("Connection failed.",
                new HttpRequestException(HttpRequestError.NameResolutionError, "No such host.")),
        };
        var sender = new AzureMailSender(client, new Resolver());

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(Tenant, new SendId("a"), Message()));

        // Assert
        _ = Assert.IsType<RequestFailedException>(exception);
    }

    /// <summary>Registration exposes the Azure sender as the application's mail sender.</summary>
    [Fact]
    public void ShouldResolveMailSenderGivenAzureMailRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAzureMailTenantResolver, Resolver>();
        _ = services.AddAzureMail(options => options.UseConnectionString(
            "endpoint=https://example.communication.azure.com/;accesskey=" + Convert.ToBase64String(new byte[32])));
        using var provider = services.BuildServiceProvider();

        // Act
        var sender = provider.GetRequiredService<IMailSender>();

        // Assert
        _ = Assert.IsType<AzureMailSender>(sender);
    }

    static MailMessage Message() => new([Recipient], "Subject", "Text");

    sealed class Resolver : IAzureMailTenantResolver
    {
        public ValueTask<AzureMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new AzureMailIdentity("sender@tenant.example"));
    }

    sealed class StubEmailClient : EmailClient
    {
        public List<(WaitUntil Wait, EmailMessage Message)> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public override Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((wait, message));
            return Failure is null
                ? Task.FromResult(new EmailSendOperation($"operation-{Requests.Count}", this))
                : Task.FromException<EmailSendOperation>(Failure);
        }
    }
}
