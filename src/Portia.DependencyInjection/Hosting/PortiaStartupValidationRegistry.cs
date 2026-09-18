namespace Cntryl.Portia;

sealed class PortiaStartupValidationRegistry
{
    readonly Dictionary<string, Action> _validations = new(StringComparer.Ordinal);
    TaskCompletionSource _endpointValidations = CompletedSource();
    int _pendingEndpointValidations;
    bool _validated;

    public bool HasPendingEndpointValidations
    {
        get
        {
            lock (_validations)
                return _pendingEndpointValidations != 0;
        }
    }

    public EndpointValidationRegistration BeginEndpointValidation()
    {
        lock (_validations)
        {
            if (_pendingEndpointValidations++ == 0)
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

    void CompleteEndpointValidation()
    {
        TaskCompletionSource? ready = null;
        lock (_validations)
        {
            if (--_pendingEndpointValidations == 0)
                ready = _endpointValidations;
        }

        _ = ready?.TrySetResult();
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
    }
}
