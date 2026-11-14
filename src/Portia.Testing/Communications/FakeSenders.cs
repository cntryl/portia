using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

/// <summary>One submission recorded by a fake sender.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
/// <param name="Tenant">The tenant the message was sent for.</param>
/// <param name="Id">The application's send identity.</param>
/// <param name="Message">The message.</param>
public sealed record FakeSubmission<TMessage>(TenantId Tenant, SendId Id, TMessage Message);

/// <summary>
///     Scripts provider outcomes for a fake sender. Each submission takes the next scripted outcome, or
///     is accepted when none is queued.
/// </summary>
public abstract class FakeSenderScript
{
    readonly ConcurrentQueue<Func<SendOutcome>> _script = new();
    int _accepted;

    /// <summary>Queues an acceptance.</summary>
    public void Accept() => _script.Enqueue(NextAccepted);

    /// <summary>Queues a definitive rejection.</summary>
    /// <param name="reason">The rejection reason, such as <see cref="SendRejectionReason.OptedOut" />.</param>
    public void Reject(SendRejectionReason reason)
    {
        var outcome = SendOutcome.Rejected(reason);
        _script.Enqueue(() => outcome);
    }

    /// <summary>Queues an uncertain outcome, as after a timeout once the request was sent.</summary>
    public void ReturnUnknown() => _script.Enqueue(() => SendOutcome.Unknown);

    /// <summary>Queues throttling, thrown as <see cref="SendThrottledException" />.</summary>
    /// <param name="retryAfter">The suggested delay, if any.</param>
    public void Throttle(TimeSpan? retryAfter = null) =>
        _script.Enqueue(() => throw new SendThrottledException(retryAfter));

    /// <summary>Queues a failure before the message reached the provider.</summary>
    /// <param name="exception">The exception to throw.</param>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _script.Enqueue(() => throw exception);
    }

    /// <summary>Takes the next scripted outcome.</summary>
    /// <returns>The outcome.</returns>
    protected SendOutcome Next() => _script.TryDequeue(out var next) ? next() : NextAccepted();

    internal static void ValidateIdentity(TenantId tenant, SendId id)
    {
        if (!SendId.IsValid(id.Value))
            throw new ArgumentException("A send ID is required.", nameof(id));
        if (tenant.Value is null)
            throw new ArgumentException("A tenant ID is required.", nameof(tenant));
    }

    SendOutcome NextAccepted() => SendOutcome.Accepted($"fake-{Interlocked.Increment(ref _accepted)}");
}

/// <summary>An in-memory <see cref="IMailSender" /> that records submissions and returns scripted outcomes.</summary>
public sealed class FakeMailSender : FakeSenderScript, IMailSender
{
    readonly ConcurrentQueue<FakeSubmission<MailMessage>> _submissions = new();

    /// <summary>Gets every submission that reached the fake provider, in order.</summary>
    public IReadOnlyList<FakeSubmission<MailMessage>> Submissions => [.. _submissions];

    /// <inheritdoc />
    public ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, MailMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateIdentity(tenant, id);
        ct.ThrowIfCancellationRequested();
        _submissions.Enqueue(new FakeSubmission<MailMessage>(tenant, id, message));
        return ValueTask.FromResult(Next());
    }
}

/// <summary>An in-memory <see cref="ISmsSender" /> that records submissions and returns scripted outcomes.</summary>
public sealed class FakeSmsSender : FakeSenderScript, ISmsSender
{
    readonly ConcurrentQueue<FakeSubmission<SmsMessage>> _submissions = new();

    /// <summary>Gets every submission that reached the fake provider, in order.</summary>
    public IReadOnlyList<FakeSubmission<SmsMessage>> Submissions => [.. _submissions];

    /// <inheritdoc />
    public ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, SmsMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateIdentity(tenant, id);
        ct.ThrowIfCancellationRequested();
        _submissions.Enqueue(new FakeSubmission<SmsMessage>(tenant, id, message));
        return ValueTask.FromResult(Next());
    }
}

/// <summary>Registers fake outbound communications for tests.</summary>
public static class FakeCommunicationsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers a <see cref="FakeMailSender" /> and a <see cref="FakeSmsSender" /> as the application's
    ///     <see cref="IMailSender" /> and <see cref="ISmsSender" />. Resolve the fakes to script outcomes and
    ///     inspect submissions; feed delivery events to the application's <see cref="IDeliveryEventReceiver" />.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddFakeCommunications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<FakeMailSender>();
        services.AddSingleton<FakeSmsSender>();
        services.AddSingleton<IMailSender>(provider => provider.GetRequiredService<FakeMailSender>());
        services.AddSingleton<ISmsSender>(provider => provider.GetRequiredService<FakeSmsSender>());
        return services;
    }
}
