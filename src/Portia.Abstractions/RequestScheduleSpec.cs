namespace Cntryl.Portia;

/// <summary>
///     Describes when and how a scheduled request fires, independent of which scheduling
///     technology (Fitz, or anything else) carries it.
/// </summary>
/// <param name="Cron">The cron expression describing when the request fires.</param>
/// <param name="DeliveryMode">How the request is delivered when it fires.</param>
public sealed record RequestScheduleSpec(
    string Cron,
    RequestScheduleDeliveryMode DeliveryMode = RequestScheduleDeliveryMode.One);
