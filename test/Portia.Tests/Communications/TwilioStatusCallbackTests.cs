using System.Net;
using System.Text;
using Cntryl.Portia.Communications.Twilio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks that Twilio status callbacks are verified before they become delivery events.</summary>
public sealed class TwilioStatusCallbackTests
{
    const string AuthToken = "auth-token-value";
    const string AccountSid = "ACtestaccount";
    static readonly TenantId Tenant = new("tenant one");
    static readonly Uri PublicAddress = new("https://api.example.com/twilio/status");

    /// <summary>A correctly signed callback reaches the receiver as a correlated delivery event.</summary>
    [Theory]
    [InlineData("queued", DeliveryEventKind.Sent)]
    [InlineData("sent", DeliveryEventKind.Sent)]
    [InlineData("delivered", DeliveryEventKind.Delivered)]
    [InlineData("undelivered", DeliveryEventKind.Undelivered)]
    [InlineData("failed", DeliveryEventKind.Failed)]
    public async Task ShouldHandDeliveryEventToReceiverGivenValidSignature(string status, DeliveryEventKind expected)
    {
        // Arrange
        var receiver = new RecordingReceiver();
        await using var app = await StartAsync(receiver);
        using var client = app.GetTestClient();
        var path = await CallbackPathAsync();
        var form = Form(status);
        using var request = Request(path, form, Sign(PublicAddress.GetLeftPart(UriPartial.Authority) + path, form, AuthToken));

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var delivery = Assert.Single(receiver.Events);
        Assert.Equal(Tenant, delivery.Tenant);
        Assert.Equal(CommunicationChannel.Sms, delivery.Channel);
        Assert.Equal(new SendId("order-1.shipped"), delivery.Id);
        Assert.Equal(expected, delivery.Kind);
    }

    /// <summary>A callback with a wrong, missing or foreign-account signature is refused and records nothing.</summary>
    [Theory]
    [InlineData("wrong-token", AccountSid, true)]
    [InlineData(AuthToken, "ACotheraccount", true)]
    [InlineData(AuthToken, AccountSid, false)]
    public async Task ShouldRefuseAndRecordNothingGivenInvalidSignature(string signingToken, string accountSid,
        bool sendSignature)
    {
        // Arrange
        var receiver = new RecordingReceiver();
        await using var app = await StartAsync(receiver);
        using var client = app.GetTestClient();
        var path = await CallbackPathAsync();
        var form = Form("delivered", accountSid);
        var signature = sendSignature
            ? Sign(PublicAddress.GetLeftPart(UriPartial.Authority) + path, form, signingToken)
            : null;
        using var request = Request(path, form, signature);

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(receiver.Events);
    }

    /// <summary>A signature made for one send cannot be replayed against another send's address.</summary>
    [Fact]
    public async Task ShouldRefuseGivenSignatureForDifferentSend()
    {
        // Arrange
        var receiver = new RecordingReceiver();
        await using var app = await StartAsync(receiver);
        using var client = app.GetTestClient();
        var path = await CallbackPathAsync();
        var form = Form("delivered");
        var signature = Sign(PublicAddress.GetLeftPart(UriPartial.Authority) + path, form, AuthToken);
        var otherPath = path[..path.LastIndexOf('/')] + "/order-2.shipped";
        using var request = Request(otherPath, form, signature);

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(receiver.Events);
    }

    /// <summary>A receiver failure fails the request, so Twilio does not treat the event as recorded.</summary>
    [Fact]
    public async Task ShouldFailRequestGivenReceiverFails()
    {
        // Arrange
        var receiver = new RecordingReceiver { Failure = new IOException("store unavailable") };
        await using var app = await StartAsync(receiver);
        using var client = app.GetTestClient();
        var path = await CallbackPathAsync();
        var form = Form("delivered");
        using var request = Request(path, form, Sign(PublicAddress.GetLeftPart(UriPartial.Authority) + path, form, AuthToken));

        // Act
        var exception = await Record.ExceptionAsync(async () => await client.SendAsync(request));

        // Assert
        _ = Assert.IsType<IOException>(exception);
    }

    static Dictionary<string, string> Form(string status, string accountSid = AccountSid) => new()
    {
        ["MessageSid"] = "SM123",
        ["MessageStatus"] = status,
        ["AccountSid"] = accountSid,
        ["To"] = "+15005550006",
        ["From"] = "+15005550001",
    };

    static HttpRequestMessage Request(string path, Dictionary<string, string> form, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        if (signature is not null)
            request.Headers.Add("X-Twilio-Signature", signature);
        return request;
    }

    // Twilio's documented scheme: HMAC-SHA1 over the URL followed by each sorted parameter name and value.
    static string Sign(string url, Dictionary<string, string> form, string token)
    {
        var text = new StringBuilder(url);
        foreach (var (key, value) in form.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            _ = text.Append(key).Append(value);
#pragma warning disable CA5350 // Twilio defines the callback signature as HMAC-SHA1.
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(text.ToString())));
#pragma warning restore CA5350
    }

    // Sends through the real sender so the callback path is exactly the one Twilio is given.
    static async Task<string> CallbackPathAsync()
    {
        var handler = new CaptureHandler();
        var sender = new TwilioSmsSender(new HttpClient(handler), new Resolver(), PublicAddress);
        _ = await sender.SendAsync(Tenant, new SendId("order-1.shipped"), new SmsMessage("+15005550006", "Hi"));
        return new Uri(handler.StatusCallback!).AbsolutePath;
    }

    static async Task<WebApplication> StartAsync(RecordingReceiver receiver)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton<ITwilioTenantResolver, Resolver>();
        _ = builder.Services.AddSingleton<IDeliveryEventReceiver>(receiver);
        _ = builder.Services.AddTwilioSms(options => options.UseStatusCallbacks(PublicAddress));
        var app = builder.Build();
        _ = app.MapTwilioStatusCallbacks("/twilio/status");
        await app.StartAsync();
        return app;
    }

    sealed class Resolver : ITwilioTenantResolver
    {
        public ValueTask<TwilioTenant> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new TwilioTenant(AccountSid, "SKkey", "secret", "MGservice") { AuthToken = AuthToken });
    }

    sealed class CaptureHandler : HttpMessageHandler
    {
        public string? StatusCallback { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            StatusCallback = form.Split('&').Select(pair => pair.Split('=', 2))
                .Where(pair => pair[0] == "StatusCallback").Select(pair => WebUtility.UrlDecode(pair[1])).Single();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"sid":"SM123"}""") };
        }
    }

    sealed class RecordingReceiver : IDeliveryEventReceiver
    {
        public List<DeliveryEvent> Events { get; } = [];

        public Exception? Failure { get; init; }

        public ValueTask ReceiveAsync(DeliveryEvent deliveryEvent, CancellationToken ct = default)
        {
            if (Failure is not null)
                return ValueTask.FromException(Failure);
            Events.Add(deliveryEvent);
            return ValueTask.CompletedTask;
        }
    }
}
