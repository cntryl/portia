using System.Net;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Cntryl.Portia.Communications.Aws;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks SES request construction and outcome reporting without cloud credentials.</summary>
public sealed class AwsMailSenderTests
{
    const string Recipient = "person@example.com";
    static readonly TenantId Tenant = new("tenant one");

    /// <summary>One SES request carries the tenant's sender identity and the correlation tags.</summary>
    [Fact]
    public async Task ShouldSubmitOneTaggedRequestGivenTenantIdentity()
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        var message = new MailMessage([Recipient], "Subject", "Text")
        {
            Html = "<p>Html</p>",
            ReplyTo = "reply@example.com",
        };

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("order-1.receipt"), message);

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.Equal("ses-message-1", outcome.ProviderReference);
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("sender@tenant.example", request.FromEmailAddress);
        Assert.Equal("tenant-events", request.ConfigurationSetName);
        Assert.Equal([Recipient], request.Destination.ToAddresses);
        Assert.Equal(["reply@example.com"], request.ReplyToAddresses);
        Assert.Equal("Subject", request.Content.Simple.Subject.Data);
        Assert.Equal("Text", request.Content.Simple.Body.Text.Data);
        Assert.Equal("<p>Html</p>", request.Content.Simple.Body.Html.Data);
        Assert.Equal("order-1.receipt", request.EmailTags.Single(tag => tag.Name == "portia-send").Value);
        var tenantTag = request.EmailTags.Single(tag => tag.Name == "portia-tenant").Value;
        Assert.Matches("^[A-Za-z0-9_-]+$", tenantTag);
    }

    /// <summary>A definitive SES refusal is a rejection with a provider-neutral reason, not an exception.</summary>
    [Theory]
    [InlineData(typeof(AccountSuspendedException), "suspended", SendRejectionReason.SenderNotAuthorized)]
    [InlineData(typeof(SendingPausedException), "paused", SendRejectionReason.SenderNotAuthorized)]
    [InlineData(typeof(MailFromDomainNotVerifiedException), "domain", SendRejectionReason.SenderNotAuthorized)]
    [InlineData(typeof(MessageRejectedException), "Email address is not verified.", SendRejectionReason.SenderNotAuthorized)]
    [InlineData(typeof(MessageRejectedException), "The message contains a virus.", SendRejectionReason.ContentRejected)]
    [InlineData(typeof(BadRequestException), "bad", SendRejectionReason.Other)]
    public async Task ShouldReportRejectionGivenDefinitiveSesRefusal(Type exceptionType, string providerMessage,
        SendRejectionReason expected)
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        proxy.Failure = ServiceFailure(exceptionType, providerMessage, HttpStatusCode.BadRequest);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), Message());

        // Assert
        Assert.Equal(SendStatus.Rejected, outcome.Status);
        Assert.Equal(expected, outcome.RejectionReason);
        _ = Assert.Single(proxy.Requests);
    }

    /// <summary>Throttling is thrown for the caller to pace; the sender does not retry.</summary>
    [Theory]
    [InlineData(typeof(TooManyRequestsException))]
    [InlineData(typeof(LimitExceededException))]
    public async Task ShouldThrowThrottledWithoutRetryingGivenSesThrottling(Type exceptionType)
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        proxy.Failure = ServiceFailure(exceptionType, "slow down", HttpStatusCode.TooManyRequests);

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(Tenant, new SendId("a"), Message()));

        // Assert
        _ = Assert.IsType<SendThrottledException>(exception);
        _ = Assert.Single(proxy.Requests);
    }

    /// <summary>A server error or a lost response leaves the result uncertain, and the call is not repeated.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldReportUnknownWithoutRetryingGivenUncertainProviderCall(bool serverError)
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        proxy.Failure = serverError
            ? ServiceFailure(typeof(InternalServiceErrorException), "oops", HttpStatusCode.InternalServerError)
            : new TaskCanceledException("The request timed out.");

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), Message());

        // Assert
        Assert.Equal(SendStatus.Unknown, outcome.Status);
        _ = Assert.Single(proxy.Requests);
    }

    /// <summary>Misconfiguration throws, and the provider's text, which can name recipients, is not repeated.</summary>
    [Theory]
    [InlineData(typeof(NotFoundException), HttpStatusCode.NotFound)]
    [InlineData(typeof(AmazonSimpleEmailServiceV2Exception), HttpStatusCode.Forbidden)]
    public async Task ShouldThrowWithoutProviderTextGivenMisconfiguredSender(Type exceptionType, HttpStatusCode status)
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        proxy.Failure = ServiceFailure(exceptionType, $"Refused for {Recipient}", status);

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
        var (sender, proxy) = CreateSender();
        proxy.Failure = new AmazonServiceException("Connection failed.",
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host."));

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(Tenant, new SendId("a"), Message()));

        // Assert
        _ = Assert.IsType<AmazonServiceException>(exception);
    }

    /// <summary>A canceled token stops the send before any provider request.</summary>
    [Fact]
    public async Task ShouldNotCallProviderGivenTokenCanceledBeforeDispatch()
    {
        // Arrange
        var (sender, proxy) = CreateSender();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await sender.SendAsync(Tenant, new SendId("a"), Message(), canceled.Token));

        // Assert
        _ = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Empty(proxy.Requests);
    }

    /// <summary>SDK retries could send a message twice, so a client that retries is refused.</summary>
    [Fact]
    public void ShouldRefuseClientGivenSdkRetriesEnabled()
    {
        // Arrange
        var client = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, TestSesClientProxy>();
        ((TestSesClientProxy)(object)client).Config = new AmazonSimpleEmailServiceV2Config { MaxErrorRetry = 2 };

        // Act
        var exception = Record.Exception(() => new AwsMailSender(client, new TestResolver()));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("MaxErrorRetry", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Registration exposes the SES sender as the application's mail sender.</summary>
    [Fact]
    public void ShouldResolveMailSenderGivenAwsMailRegistration()
    {
        // Arrange
        var client = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, TestSesClientProxy>();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAwsMailTenantResolver, TestResolver>();
        _ = services.AddAwsMail(options => options.UseClient(client));
        using var provider = services.BuildServiceProvider();

        // Act
        var sender = provider.GetRequiredService<IMailSender>();

        // Assert
        _ = Assert.IsType<AwsMailSender>(sender);
    }

    static MailMessage Message() => new([Recipient], "Subject", "Text");

    static (AwsMailSender Sender, TestSesClientProxy Proxy) CreateSender()
    {
        var client = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, TestSesClientProxy>();
        return (new AwsMailSender(client, new TestResolver()), (TestSesClientProxy)(object)client);
    }

    static AmazonServiceException ServiceFailure(Type type, string message, HttpStatusCode status)
    {
        var exception = (AmazonServiceException)Activator.CreateInstance(type, message)!;
        exception.StatusCode = status;
        exception.ErrorCode = type.Name.Replace("Exception", string.Empty, StringComparison.Ordinal);
        return exception;
    }

    sealed class TestResolver : IAwsMailTenantResolver
    {
        public ValueTask<AwsMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new AwsMailIdentity("sender@tenant.example", "tenant-events"));
    }

    /// <summary>A scripted SES client that records each send request.</summary>
    public class TestSesClientProxy : DispatchProxy
    {
        /// <summary>Gets the send requests that reached the client.</summary>
        public List<SendEmailRequest> Requests { get; } = [];

        /// <summary>Gets or sets the failure every send throws.</summary>
        public Exception? Failure { get; set; }

        /// <summary>Gets or sets the client configuration.</summary>
        public IClientConfig Config { get; set; } = new AmazonSimpleEmailServiceV2Config { MaxErrorRetry = 0 };

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Config":
                    return Config;
                case nameof(IAmazonSimpleEmailServiceV2.SendEmailAsync):
                    Requests.Add((SendEmailRequest)args![0]!);
                    return Failure is null
                        ? Task.FromResult(new SendEmailResponse { MessageId = $"ses-message-{Requests.Count}" })
                        : Task.FromException<SendEmailResponse>(Failure);
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
