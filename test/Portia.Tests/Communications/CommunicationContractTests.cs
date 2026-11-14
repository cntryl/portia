namespace Cntryl.Portia.Tests.Communications;

/// <summary>Provider-neutral message, identity and outcome contracts.</summary>
public sealed class CommunicationContractTests
{
    /// <summary>Validated recipients cannot be changed through the exposed collection.</summary>
    [Fact]
    public void ShouldPreserveValidatedRecipientsGivenAttemptedMutation()
    {
        // Arrange
        var message = new MailMessage(["original@example.com"], "Subject", "Body");

        // Act
        var exception = Record.Exception(() => ((IList<string>)message.To)[0] = "replacement@example.com");

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal(["original@example.com"], message.To);
    }

    /// <summary>The fake mail sender rejects invalid identities without recording a submission.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRejectInvalidIdentityGivenFakeMailSender(bool invalidTenant)
    {
        // Arrange
        var sender = new FakeMailSender();
        var tenant = invalidTenant ? default : new TenantId("tenant-one");
        var id = invalidTenant ? new SendId("receipt") : default;
        var message = new MailMessage(["person@example.com"], "Subject", "Body");

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(tenant, id, message));

        // Assert
        Assert.Equal(invalidTenant ? "tenant" : "id", Assert.IsType<ArgumentException>(exception).ParamName);
        Assert.Empty(sender.Submissions);
    }

    /// <summary>The fake SMS sender rejects invalid identities without recording a submission.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRejectInvalidIdentityGivenFakeSmsSender(bool invalidTenant)
    {
        // Arrange
        var sender = new FakeSmsSender();
        var tenant = invalidTenant ? default : new TenantId("tenant-one");
        var id = invalidTenant ? new SendId("receipt") : default;
        var message = new SmsMessage("+15005550006", "Hi");

        // Act
        var exception = await Record.ExceptionAsync(async () => await sender.SendAsync(tenant, id, message));

        // Assert
        Assert.Equal(invalidTenant ? "tenant" : "id", Assert.IsType<ArgumentException>(exception).ParamName);
        Assert.Empty(sender.Submissions);
    }

    /// <summary>Messages and their validation failures never print recipients or content.</summary>
    [Fact]
    public void ShouldRedactRecipientsAndContentGivenMessagesAndInvalidInput()
    {
        // Arrange
        var mail = new MailMessage(["person@example.com"], "Secret subject", "Secret text");
        var sms = new SmsMessage("+15005550006", "Secret body");

        // Act
        var invalidMail = Record.Exception(() => new MailMessage(["not an address"], "Subject", "Text"));
        var invalidSms = Record.Exception(() => new SmsMessage("555-0100 secret", "Body"));

        // Assert
        var printed = string.Join('|', mail, sms, invalidMail?.Message, invalidSms?.Message);
        foreach (var secret in new[] { "person@example.com", "Secret subject", "Secret text", "Secret body",
                     "+15005550006", "not an address", "555-0100 secret" })
            Assert.DoesNotContain(secret, printed, StringComparison.Ordinal);
        _ = Assert.IsType<ArgumentException>(invalidMail);
        _ = Assert.IsType<ArgumentException>(invalidSms);
    }

    /// <summary>A subject with a line break could inject headers, so it is refused.</summary>
    [Theory]
    [InlineData("Hello\r\nBcc: someone@example.com")]
    [InlineData("   ")]
    public void ShouldRefuseSubjectGivenLineBreakOrBlank(string subject)
    {
        // Arrange
        string[] to = ["person@example.com"];

        // Act
        var exception = Record.Exception(() => new MailMessage(to, subject, "Text"));

        // Assert
        _ = Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Send identities are restricted to characters every provider can carry.</summary>
    [Theory]
    [InlineData("order-1.receipt_v2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("person@example.com", false)]
    [InlineData("ümlaut", false)]
    public void ShouldAcceptOnlyPortableTextGivenSendId(string value, bool valid)
    {
        // Arrange
        var text = value;

        // Act
        var exception = Record.Exception(() => new SendId(text));

        // Assert
        Assert.Equal(valid, exception is null);
        Assert.Equal(valid, SendId.IsValid(text));
    }

    /// <summary>The fake senders record submissions and return scripted outcomes.</summary>
    [Fact]
    public async Task ShouldReturnScriptedOutcomesGivenFakeSmsSender()
    {
        // Arrange
        using var provider = new ServiceCollection().AddFakeCommunications().BuildServiceProvider();
        var fake = provider.GetRequiredService<FakeSmsSender>();
        var sender = provider.GetRequiredService<ISmsSender>();
        var tenant = new TenantId("tenant-a");
        var message = new SmsMessage("+15005550006", "Hi");
        fake.Reject(SendRejectionReason.OptedOut);
        fake.ReturnUnknown();
        fake.Throttle(TimeSpan.FromSeconds(2));

        // Act
        var rejected = await sender.SendAsync(tenant, new SendId("a"), message);
        var unknown = await sender.SendAsync(tenant, new SendId("b"), message);
        var throttled = await Record.ExceptionAsync(async () => await sender.SendAsync(tenant, new SendId("c"), message));
        var accepted = await sender.SendAsync(tenant, new SendId("d"), message);

        // Assert
        Assert.Equal(SendRejectionReason.OptedOut, rejected.RejectionReason);
        Assert.Equal(SendStatus.Unknown, unknown.Status);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<SendThrottledException>(throttled).RetryAfter);
        Assert.Equal(SendStatus.Accepted, accepted.Status);
        Assert.NotNull(accepted.ProviderReference);
        Assert.Equal(["a", "b", "c", "d"], fake.Submissions.Select(s => s.Id.Value));
    }
}
