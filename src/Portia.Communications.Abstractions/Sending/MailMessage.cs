namespace Cntryl.Portia.Communications;

/// <summary>
///     An outbound email: recipients, a subject and a plain-text body, with optional HTML and reply-to.
///     <see cref="ToString" /> and validation failures never include recipients or content.
/// </summary>
public sealed class MailMessage
{
    /// <summary>The most recipients one message accepts.</summary>
    public const int MaximumRecipients = 50;

    /// <summary>Creates an email.</summary>
    /// <param name="to">The recipient addresses, 1 to <see cref="MaximumRecipients" />.</param>
    /// <param name="subject">The single-line subject.</param>
    /// <param name="text">The plain-text body.</param>
    /// <exception cref="ArgumentException">A value is missing or malformed.</exception>
    public MailMessage(IEnumerable<string> to, string subject, string text)
    {
        ArgumentNullException.ThrowIfNull(to);
        var recipients = to.ToArray();
        if (recipients.Length is 0 or > MaximumRecipients)
            throw new ArgumentException($"An email needs 1-{MaximumRecipients} recipients.", nameof(to));
        if (!recipients.All(IsAddress))
            throw new ArgumentException("An email recipient is not a valid address.", nameof(to));
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 998 || subject.Any(char.IsControl))
            throw new ArgumentException("An email subject must be one nonblank line of at most 998 characters.",
                nameof(subject));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("An email needs a nonblank plain-text body.", nameof(text));
        To = Array.AsReadOnly(recipients);
        Subject = subject;
        Text = text;
    }

    /// <summary>Gets the recipient addresses.</summary>
    public IReadOnlyList<string> To { get; }

    /// <summary>Gets the subject.</summary>
    public string Subject { get; }

    /// <summary>Gets the plain-text body.</summary>
    public string Text { get; }

    /// <summary>Gets the optional HTML body sent alongside the plain text.</summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public string? Html
    {
        get;
        init => field = value is null || !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An HTML body cannot be blank.", nameof(value));
    }

    /// <summary>Gets the optional reply-to address.</summary>
    /// <exception cref="ArgumentException">The value is not a valid address.</exception>
    public string? ReplyTo
    {
        get;
        init => field = value is null || IsAddress(value)
            ? value
            : throw new ArgumentException("The reply-to value is not a valid address.", nameof(value));
    }

    /// <inheritdoc />
    public override string ToString() => $"MailMessage {{ Recipients = {To.Count}, Content = [redacted] }}";


    static bool IsAddress(string? value)
    {
        if (value is not { Length: > 2 and <= 320 })
            return false;
        var at = value.LastIndexOf('@');
        return at > 0 && at < value.Length - 1
                      && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or ';' or '<' or '>');
    }
}

/// <summary>
///     An outbound SMS: one E.164 recipient number and a text body. <see cref="ToString" /> and
///     validation failures never include the number or the text.
/// </summary>
public sealed class SmsMessage
{
    /// <summary>The longest accepted body, in UTF-16 code units.</summary>
    public const int MaximumBodyLength = 1600;

    /// <summary>Creates an SMS.</summary>
    /// <param name="to">The recipient number in E.164 form, such as <c>+15005550006</c>.</param>
    /// <param name="body">The text, 1 to <see cref="MaximumBodyLength" /> characters.</param>
    /// <exception cref="ArgumentException">A value is missing or malformed.</exception>
    public SmsMessage(string to, string body)
    {
        if (!IsE164(to))
            throw new ArgumentException("An SMS recipient must be an E.164 number.", nameof(to));
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaximumBodyLength)
            throw new ArgumentException($"An SMS body must be 1-{MaximumBodyLength} nonblank characters.",
                nameof(body));
        To = to;
        Body = body;
    }

    /// <summary>Gets the recipient number.</summary>
    public string To { get; }

    /// <summary>Gets the text.</summary>
    public string Body { get; }

    /// <inheritdoc />
    public override string ToString() => "SmsMessage { Recipient = [redacted], Body = [redacted] }";


    static bool IsE164(string? value) =>
        value is { Length: >= 3 and <= 16 } && value[0] == '+' && value[1] is >= '1' and <= '9'
        && value.Skip(2).All(char.IsAsciiDigit);
}
