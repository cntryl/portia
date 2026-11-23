namespace Cntryl.Portia;

// What an authorizer and a guard have in common: each answers one request with a Result, in order,
// and the first failure settles the request. They differ only in lifecycle phase and in the metric
// they record, so both run through RequestCheckRunner.
interface IRequestCheck
{
    Type Owner { get; }

    string Role { get; }

    ValueTask<Result> CheckAsync(
        IServiceProvider services,
        IRequestBase request,
        IRequestContext context,
        CancellationToken ct
    );

    void Finished(long started, string outcome);
}
