using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cntryl.Portia.Communications.Twilio;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks Twilio request construction and outcome reporting without network access.</summary>
public sealed class TwilioSmsSenderTests
{
    const string Recipient = "+15005550006";
    const string Secret = "api-key-secret-value";
    static readonly TenantId Tenant = new("tenant one");
    static readonly Uri Callbacks = new("https://api.example.com/twilio/status");

    /// <summary>One form request carries the tenant's credentials, sender and correlated callback address.</summary>
    [Fact]
    public async Task ShouldPostOneRequestGivenTenantCredentials()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.Created, """{"sid":"SM123","status":"queued"}""");
        var sender = CreateSender(handler, Callbacks);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("order-1.shipped"), new SmsMessage(Recipient, "Hi there"));

        // Assert
        Assert.Equal(SendStatus.Accepted, outcome.Status);
        Assert.Equal("SM123", outcome.ProviderReference);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/ACtestaccount/Messages.json",
            request.Uri.AbsoluteUri);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"SKkey:{Secret}")), request.Authorization?.Parameter);
        Assert.Equal(Recipient, request.Form["To"]);
        Assert.Equal("Hi there", request.Form["Body"]);
        Assert.Equal("MGservice", request.Form["MessagingServiceSid"]);
        Assert.False(request.Form.ContainsKey("From"));
        Assert.Matches("^https://api\\.example\\.com/twilio/status/[A-Za-z0-9_-]+/order-1\\.shipped$",
            request.Form["StatusCallback"]);
    }

    /// <summary>A from number is sent as the sender, and no callback is requested unless one is configured.</summary>
    [Fact]
    public async Task ShouldSendFromNumberWithoutCallbackGivenNumberSenderAndNoCallbackAddress()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.Created, """{"sid":"SM123"}""");
        var sender = new TwilioSmsSender(new HttpClient(handler),
            new Resolver(new TwilioTenant("ACtestaccount", "SKkey", Secret, "+15005550001")), null);

        // Act
        _ = await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi"));

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("+15005550001", request.Form["From"]);
        Assert.False(request.Form.ContainsKey("MessagingServiceSid"));
        Assert.False(request.Form.ContainsKey("StatusCallback"));
    }

    /// <summary>A definitive Twilio refusal is a rejection with a stable reason; opt-out is distinguishable.</summary>
    [Theory]
    [InlineData(21610, SendRejectionReason.OptedOut)]
    [InlineData(21211, SendRejectionReason.InvalidRecipient)]
    [InlineData(21614, SendRejectionReason.InvalidRecipient)]
    [InlineData(21408, SendRejectionReason.SenderNotAuthorized)]
    [InlineData(21606, SendRejectionReason.SenderNotAuthorized)]
    [InlineData(21617, SendRejectionReason.ContentRejected)]
    [InlineData(99999, SendRejectionReason.Other)]
    public async Task ShouldReportRejectionGivenDefinitiveTwilioRefusal(int code, SendRejectionReason expected)
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            $$"""{"code":{{code}},"message":"Refused for {{Recipient}}","status":400}""");
        var sender = CreateSender(handler, Callbacks);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi"));

        // Assert
        Assert.Equal(SendStatus.Rejected, outcome.Status);
        Assert.Equal(expected, outcome.RejectionReason);
        _ = Assert.Single(handler.Requests);
    }

    /// <summary>HTTP 429 is thrown with Twilio's suggested delay; the sender does not retry.</summary>
    [Fact]
    public async Task ShouldThrowThrottledWithoutRetryingGivenTooManyRequests()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, """{"code":20429,"status":429}""")
        {
            RetryAfter = TimeSpan.FromSeconds(3),
        };
        var sender = CreateSender(handler, Callbacks);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi")));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<SendThrottledException>(exception).RetryAfter);
        _ = Assert.Single(handler.Requests);
    }

    /// <summary>A server error or a lost response leaves the result uncertain, and the call is not repeated.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldReportUnknownWithoutRetryingGivenUncertainProviderCall(bool serverError)
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "unavailable")
        {
            Failure = serverError ? null : new TaskCanceledException("The request timed out."),
        };
        var sender = CreateSender(handler, Callbacks);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi"));

        // Assert
        Assert.Equal(SendStatus.Unknown, outcome.Status);
        _ = Assert.Single(handler.Requests);
    }

    /// <summary>A broken established connection can lose a response after the message was accepted.</summary>
    [Fact]
    public async Task ShouldReportUnknownGivenConnectionFailureAfterDispatch()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.OK, "{}")
        {
            Failure = new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset."),
        };
        var sender = CreateSender(handler, Callbacks);

        // Act
        var outcome = await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi"));

        // Assert
        Assert.Equal(SendStatus.Unknown, outcome.Status);
        _ = Assert.Single(handler.Requests);
    }

    /// <summary>Refused credentials throw, and neither the secret nor the recipient is repeated.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ShouldThrowWithoutSecretsGivenRefusedCredentials(HttpStatusCode status)
    {
        // Arrange
        var handler = new StubHandler(status, $$"""{"code":20003,"message":"Authenticate {{Recipient}} {{Secret}}"}""");
        var sender = CreateSender(handler, Callbacks);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi")));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.DoesNotContain(Recipient, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A connection that was never established certainly sent nothing, so it stays a retryable failure.</summary>
    [Fact]
    public async Task ShouldRethrowGivenConnectionNeverEstablished()
    {
        // Arrange
        var handler = new StubHandler(HttpStatusCode.OK, "{}")
        {
            Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host."),
        };
        var sender = CreateSender(handler, Callbacks);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await sender.SendAsync(Tenant, new SendId("a"), new SmsMessage(Recipient, "Hi")));

        // Assert
        _ = Assert.IsType<HttpRequestException>(exception);
    }

    /// <summary>Tenant credentials never print their secrets.</summary>
    [Fact]
    public void ShouldRedactSecretsGivenTenantPrinted()
    {
        // Arrange
        var tenant = new TwilioTenant("ACtestaccount", "SKkey", Secret, "MGservice")
        {
            AuthToken = "auth-token-value",
        };

        // Act
        var printed = tenant.ToString();

        // Assert
        Assert.DoesNotContain(Secret, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-token-value", printed, StringComparison.Ordinal);
    }

    /// <summary>Registration exposes the Twilio sender as the application's SMS sender.</summary>
    [Fact]
    public void ShouldResolveSmsSenderGivenTwilioRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<ITwilioTenantResolver>(new Resolver(Account()));
        _ = services.AddTwilioSms(options => options.UseStatusCallbacks(Callbacks));
        using var provider = services.BuildServiceProvider();

        // Act
        var sender = provider.GetRequiredService<ISmsSender>();

        // Assert
        _ = Assert.IsType<TwilioSmsSender>(sender);
    }

    static TwilioTenant Account() => new("ACtestaccount", "SKkey", Secret, "MGservice");

    static TwilioSmsSender CreateSender(StubHandler handler, Uri? callbacks) =>
        new(new HttpClient(handler), new Resolver(Account()), callbacks);

    sealed class Resolver(TwilioTenant tenant) : ITwilioTenantResolver
    {
        public ValueTask<TwilioTenant> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(tenant);
    }

    sealed record CapturedRequest(HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization,
        Dictionary<string, string> Form);

    sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public TimeSpan? RetryAfter { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Headers.Authorization,
                form.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(
                    pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]))));
            if (Failure is not null)
                throw Failure;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (RetryAfter is { } delay)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            return response;
        }
    }
}
