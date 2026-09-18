namespace Cntryl.Portia;

interface IRequestScheduleDeclaration
{
    void Validate();
    ValueTask<string> EnsureAsync(IRequestScheduler scheduler, CancellationToken ct);
}
