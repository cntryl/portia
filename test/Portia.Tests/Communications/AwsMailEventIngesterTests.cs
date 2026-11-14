using System.Text.Json;
using Amazon.SimpleEmailV2;
using Cntryl.Portia.Communications.Aws;

namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks that SES events become delivery events correlated to the application's send identity.</summary>
public sealed class AwsMailEventIngesterTests
{
    static readonly TenantId Tenant = new("tenant one");
    static readonly string[] Destination = ["person@example.com"];

    /// <summary>Each SES event type maps to a provider-neutral kind, whichever envelope carried it.</summary>
    [Theory]
    [InlineData("Send", "send", DeliveryEventKind.Sent, "raw")]
    [InlineData("Delivery", "delivery", DeliveryEventKind.Delivered, "sns")]
    [InlineData("Bounce", "bounce", DeliveryEventKind.Bounced, "eventbridge")]
    [InlineData("Complaint", "complaint", DeliveryEventKind.Complained, "raw")]
    [InlineData("Reject", "reject", DeliveryEventKind.Rejected, "raw")]
    [InlineData("DeliveryDelay", "deliveryDelay", DeliveryEventKind.Delayed, "raw")]
    [InlineData("Rendering Failure", "failure", DeliveryEventKind.Failed, "raw")]
    public async Task ShouldHandCorrelatedEventToReceiverGivenSesEvent(string eventType, string detailProperty,
        DeliveryEventKind expected, string envelope)
    {
        // Arrange
        var receiver = new RecordingReceiver();
        var ingester = new AwsMailEventIngester(receiver);
        var json = Wrap(envelope, await SesEventAsync(eventType, detailProperty));

        // Act
        var ingested = await ingester.IngestAsync(json);

        // Assert
        Assert.True(ingested);
        var delivery = Assert.Single(receiver.Events);
        Assert.Equal(new DeliveryEvent(Tenant, CommunicationChannel.Mail, new SendId("order-1.receipt"), expected,
            DateTimeOffset.Parse("2026-11-13T10:00:05Z", System.Globalization.CultureInfo.InvariantCulture)), delivery);
    }

    /// <summary>Mail that Portia did not send, and engagement events, are not delivery events.</summary>
    [Theory]
    [InlineData("""{"eventType":"Delivery","mail":{"timestamp":"2026-11-13T10:00:00Z","tags":{"ses:from-domain":["example.com"]}}}""")]
    [InlineData("""{"eventType":"Open","mail":{"timestamp":"2026-11-13T10:00:00Z","tags":{"portia-tenant":["dGVuYW50"],"portia-send":["a"]}}}""")]
    [InlineData("""{"eventType":"Delivery","mail":{"timestamp":"2026-11-13T10:00:00Z","tags":{"portia-tenant":["dGVuYW50"],"portia-send":["not a send id"]}}}""")]
    [InlineData("""{"Type":"SubscriptionConfirmation","Message":"confirm"}""")]
    public async Task ShouldIgnoreEventGivenNoPortiaDeliveryEvent(string json)
    {
        // Arrange
        var receiver = new RecordingReceiver();
        var ingester = new AwsMailEventIngester(receiver);

        // Act
        var ingested = await ingester.IngestAsync(json);

        // Assert
        Assert.False(ingested);
        Assert.Empty(receiver.Events);
    }

    /// <summary>Text that is not JSON is refused without repeating it.</summary>
    [Fact]
    public async Task ShouldThrowWithoutPayloadGivenMalformedEvent()
    {
        // Arrange
        var ingester = new AwsMailEventIngester(new RecordingReceiver());

        // Act
        var exception = await Record.ExceptionAsync(async () => await ingester.IngestAsync("person@example.com {"));

        // Assert
        var failure = Assert.IsType<FormatException>(exception);
        Assert.DoesNotContain("person@example.com", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A receiver failure propagates so the event source delivers the event again.</summary>
    [Fact]
    public async Task ShouldPropagateFailureGivenReceiverFails()
    {
        // Arrange
        var receiver = new RecordingReceiver { Failure = new IOException("store unavailable") };
        var ingester = new AwsMailEventIngester(receiver);
        var json = await SesEventAsync("Delivery", "delivery");

        // Act
        var exception = await Record.ExceptionAsync(async () => await ingester.IngestAsync(json));

        // Assert
        _ = Assert.IsType<IOException>(exception);
    }

    // Sends through the real sender so the event carries exactly the tags the sender attaches.
    static async Task<string> SesEventAsync(string eventType, string detailProperty)
    {
        var client = DispatchProxy.Create<IAmazonSimpleEmailServiceV2, AwsMailSenderTests.TestSesClientProxy>();
        var sender = new AwsMailSender(client, new Resolver());
        _ = await sender.SendAsync(Tenant, new SendId("order-1.receipt"),
            new MailMessage(["person@example.com"], "Subject", "Text"));
        var request = ((AwsMailSenderTests.TestSesClientProxy)(object)client).Requests.Single();
        var tags = request.EmailTags.ToDictionary(tag => tag.Name, tag => new[] { tag.Value });
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["eventType"] = eventType,
            ["mail"] = new Dictionary<string, object>
            {
                ["timestamp"] = "2026-11-13T10:00:00.000Z",
                ["destination"] = Destination,
                ["tags"] = tags,
            },
            [detailProperty] = new Dictionary<string, object> { ["timestamp"] = "2026-11-13T10:00:05.000Z" },
        });
    }

    static string Wrap(string envelope, string sesEvent) => envelope switch
    {
        "sns" => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Type"] = "Notification",
            ["Message"] = sesEvent,
        }),
        "eventbridge" => $$"""{"detail-type":"Email Bounced","source":"aws.ses","detail":{{sesEvent}}}""",
        _ => sesEvent,
    };

    sealed class Resolver : IAwsMailTenantResolver
    {
        public ValueTask<AwsMailIdentity> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(new AwsMailIdentity("sender@tenant.example", "tenant-events"));
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
