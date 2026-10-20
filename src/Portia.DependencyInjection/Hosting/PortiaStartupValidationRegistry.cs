using System.Runtime.ExceptionServices;

namespace Cntryl.Portia;

sealed class PortiaStartupValidationRegistry
{
    readonly Dictionary<string, Action> _validations = new(StringComparer.Ordinal);
    TaskCompletionSource _endpointValidations = CompletedSource();
    int _pendingEndpointValidations;
    Exception? _endpointValidationError;
    bool _validated;

    public bool HasPendingEndpointValidations
    {
        get
        {
            lock (_validations)
                return _pendingEndpointValidations != 0 || _endpointValidationError is not null;
        }
    }

    public EndpointValidationRegistration BeginEndpointValidation()
    {
        lock (_validations)
        {
            if (_pendingEndpointValidations++ == 0 && _endpointValidationError is null)
                _endpointValidations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new EndpointValidationRegistration(this);
    }

    public Task WaitForEndpointValidationsAsync(CancellationToken ct)
    {
        Task ready;
        lock (_validations)
            ready = _endpointValidations.Task;
        return ready.WaitAsync(ct);
    }

    public void ThrowIfEndpointValidationFailed()
    {
        Exception? error;
        lock (_validations)
            error = _endpointValidationError;
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);
    }

    public void Add(string name, Action validation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(validation);
        lock (_validations)
        {
            if (_validated)
            {
                validation();
                return;
            }

            _validations.TryAdd(name, validation);
        }
    }

    public void Validate()
    {
        lock (_validations)
        {
            foreach (var validation in _validations.OrderBy(item => item.Key, StringComparer.Ordinal))
                validation.Value();
            _validated = true;
        }
    }

    void CompleteEndpointValidation(Exception? error = null)
    {
        lock (_validations)
        {
            _pendingEndpointValidations--;
            _endpointValidationError ??= error;
            // A captured web-host error still blocks subsequent workers. Keep the first failure
            // across later mappings and never publish a successful gate over a failed one.
            if (_endpointValidationError is { } failure)
                _endpointValidations.TrySetException(failure);
            else if (_pendingEndpointValidations == 0)
                _endpointValidations.TrySetResult();
        }
    }

    static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    internal sealed class EndpointValidationRegistration(PortiaStartupValidationRegistry owner)
    {
        int _completed;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                owner.CompleteEndpointValidation();
        }

        public void Fail(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                owner.CompleteEndpointValidation(error);
        }
    }
}
