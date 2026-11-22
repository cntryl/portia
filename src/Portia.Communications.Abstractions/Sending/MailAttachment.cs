using System.Collections.Immutable;
using System.Net.Http.Headers;

namespace Cntryl.Portia.Communications;

/// <summary>An immutable file attached to one outbound <see cref="MailMessage"/>.</summary>
public sealed class MailAttachment
{
    /// <summary>The largest content snapshot accepted for one attachment.</summary>
    public const int MaximumContentBytes = 5 * 1024 * 1024;

    /// <summary>Creates an attachment by taking an owned snapshot of its content.</summary>
    /// <param name="fileName">A display filename without directory components.</param>
    /// <param name="mediaType">A MIME media type without parameters.</param>
    /// <param name="content">The binary content to snapshot.</param>
    /// <exception cref="ArgumentException">A value is invalid or the content exceeds the limit.</exception>
    public MailAttachment(string fileName, string mediaType, ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName.Any(char.IsControl)
            || fileName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || fileName is "." or "..")
            throw new ArgumentException("An attachment filename must be a single nonblank name of at most 255 characters.",
                nameof(fileName));
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Any(char.IsControl)
            || !MediaTypeHeaderValue.TryParse(mediaType, out var parsed) || parsed.Parameters.Count != 0
            || parsed.MediaType!.Contains('*', StringComparison.Ordinal))
            throw new ArgumentException("An attachment media type must be a MIME type without parameters.",
                nameof(mediaType));
        if (content.IsEmpty || content.Length > MaximumContentBytes)
            throw new ArgumentException($"Attachment content must be 1-{MaximumContentBytes} bytes.", nameof(content));

        FileName = fileName;
        MediaType = parsed!.MediaType!;
        Content = ImmutableArray.Create(content);
    }

    /// <summary>Gets the display filename.</summary>
    public string FileName { get; }

    /// <summary>Gets the MIME media type.</summary>
    public string MediaType { get; }

    /// <summary>Gets an immutable snapshot of the file bytes.</summary>
    public ImmutableArray<byte> Content { get; }

    /// <inheritdoc />
    public override string ToString() => $"MailAttachment {{ File = [redacted], Content = [redacted] }}";
}
