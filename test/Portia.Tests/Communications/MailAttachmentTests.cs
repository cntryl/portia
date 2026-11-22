namespace Cntryl.Portia.Tests.Communications;

/// <summary>Checks attachment validation, ownership and redaction.</summary>
public sealed class MailAttachmentTests
{
    /// <summary>Attachment bytes are copied and diagnostics redact filename, media type and content.</summary>
    [Fact]
    public void ShouldOwnContentAndRedactGivenAttachment()
    {
        // Arrange
        var source = new byte[] { 1, 2, 3 };

        // Act
        var attachment = new MailAttachment("receipt.pdf", "application/pdf", source);
        source[0] = 9;

        // Assert
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Content);
        Assert.DoesNotContain("receipt.pdf", attachment.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("application/pdf", attachment.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1, 2, 3", attachment.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Names cannot carry path components and media types cannot be wildcards or parameterized.</summary>
    [Theory]
    [InlineData("../secret.pdf", "application/pdf")]
    [InlineData("receipt.pdf", "application/*")]
    [InlineData("receipt.pdf", "application/pdf; charset=utf-8")]
    public void ShouldRejectUnsafeMetadataGivenAttachment(string fileName, string mediaType)
    {
        // Arrange
        var content = new byte[] { 1, 2, 3 };

        // Act
        var exception = Record.Exception(() => new MailAttachment(fileName, mediaType, content));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.DoesNotContain(fileName, exception!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(mediaType, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1, 2, 3", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Messages snapshot their collection and reject excess count, size and null attachments.</summary>
    [Fact]
    public void ShouldSnapshotAndBoundAttachmentsGivenMailMessage()
    {
        // Arrange
        var attachment = new MailAttachment("receipt.pdf", "application/pdf", [1, 2, 3]);
        var attachments = new List<MailAttachment> { attachment };
        var oversized = new[]
        {
            new MailAttachment("first.bin", "application/octet-stream", new byte[3 * 1024 * 1024]),
            new MailAttachment("second.bin", "application/octet-stream", new byte[3 * 1024 * 1024]),
        };

        // Act
        var message = new MailMessage(["person@example.com"], "subject", "body") { Attachments = attachments };
        attachments.Clear();
        var tooLarge = Record.Exception(() => WithAttachments(oversized));
        var tooMany = Record.Exception(() => WithAttachments(
            Enumerable.Repeat(attachment, MailMessage.MaximumAttachments + 1).ToArray()));
        var nullEntry = Record.Exception(() => WithAttachments(new MailAttachment[] { null! }));

        // Assert
        Assert.Same(attachment, Assert.Single(message.Attachments));
        Assert.IsType<ArgumentException>(tooLarge);
        Assert.DoesNotContain("first.bin", tooLarge!.ToString(), StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(tooMany);
        Assert.IsType<ArgumentException>(nullEntry);
        Assert.Empty(new MailMessage(["person@example.com"], "subject", "body").Attachments);
    }

    static MailMessage WithAttachments(IReadOnlyList<MailAttachment> attachments) =>
        new(["person@example.com"], "subject", "body") { Attachments = attachments };
}
