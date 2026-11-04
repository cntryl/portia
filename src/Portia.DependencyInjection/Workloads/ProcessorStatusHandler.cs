using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class ProcessorStatusHandler(
    IServiceScopeFactory scopes,
    IEnumerable<WorkloadRegistration> registrations,
    TimeProvider? timeProvider = null) : IRequestHandler<GetProcessorStatus, ProcessorStatus>
{
    readonly WorkloadRegistration[] _registrations = [.. registrations];
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<Result<ProcessorStatus>> HandleAsync(IRequestContext<GetProcessorStatus> context,
        CancellationToken ct)
    {
        var request = context.Request;
        var registration = _registrations.FirstOrDefault(item => item.Name == request.Processor);
        if (registration is null)
            return RequestError.NotFound("The requested processor is not registered.");

        TenantId? tenant = null;
        if (request.Tenant is { } tenantValue)
        {
            if (!TenantId.TryParse(tenantValue, null, out var parsed))
                return RequestError.Validation("Processor status requires a valid tenant identity.");
            tenant = parsed;
        }
        if (registration.Scope == WorkloadScope.PerTenant && tenant is null ||
            registration.Scope == WorkloadScope.Global && tenant is not null)
            return RequestError.Validation("Processor status must match the registered tenant or global scope.");

        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var identity = new WorkloadIdentity(registration.Name, tenant);
        provider.GetRequiredService<WorkloadContext>().Initialize(identity, registration.Name);
        registration.Descriptor.Bind(provider, identity, registration.Name);
        var pattern = registration.Descriptor.Pattern(provider);
        var checkpoint = await registration.Descriptor.LoadCheckpoint(provider, registration.Processing, ct)
            .ConfigureAwait(false);
        var reader = provider.GetService<IDomainEventProgressReader>() ??
                     provider.GetService<IDomainEventReader>() as IDomainEventProgressReader;
        var progress = new EventStreamProgress(checkpoint.Cursor, null, null, null, _clock.GetUtcNow());
        string? sourceError = null;
        if (reader is not null)
        {
            try
            {
                var observation = await reader.GetProgressAsync(pattern, checkpoint.Cursor, ct).ConfigureAwait(false);
                if (observation.Position != checkpoint.Cursor)
                    throw new InvalidOperationException("Source progress must preserve the supplied committed checkpoint.");
                progress = observation;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                sourceError = exception.GetType().Name;
                progress = progress with { ObservedAt = _clock.GetUtcNow() };
            }
        }

        ProcessorStatusFailure? failure = null;
        string? failureError = null;
        if (provider.GetService<IOperationalStore>() is { } journal)
        {
            var target = new OperationalTarget(registration.Name, pattern.ToString(),
                new OperationalScope(request.Tenant), registration.Processing.RebuildId);
            OperationalProcessorState? state = null;
            try
            {
                state = await journal.ReadAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failureError = exception.GetType().Name;
            }
            // The native checkpoint remains authoritative even if operational confirmation was lost.
            // Old failure evidence must not mark an already advanced projector as blocked.
            if (state is not null && state.Checkpoint == checkpoint.Cursor.Value &&
                (state.ConsecutiveFailures > 0 || state.Block is not null))
            {
                var attempt = state.Block is { } block
                    ? state.Attempts.LastOrDefault(item => item.Id == block.AttemptId)
                    : state.Attempts.LastOrDefault(item => item.Outcome == "failed");
                failure = new ProcessorStatusFailure(attempt?.FailureStage, attempt?.FailureKind,
                    attempt?.IsTransient, state.ConsecutiveFailures, state.Block is not null, attempt?.CompletedAt);
            }
        }

        return new ProcessorStatus(registration.Name, registration.Descriptor.SupportsRebuild ? "projector" : "reactor",
            request.Tenant, pattern.ToString(), registration.Processing.RebuildId, progress, failure, sourceError,
            failureError);
    }
}
