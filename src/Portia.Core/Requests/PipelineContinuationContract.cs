namespace Cntryl.Portia;

static class PipelineContinuationContract
{
    internal const string SingleUseMessage =
        "A request pipeline continuation may be invoked at most once and only during its behavior invocation.";
}
