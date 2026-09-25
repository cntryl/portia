namespace Cntryl.Portia.Storage;

/// <summary>A bounded, short-lived object download destination.</summary>
public sealed record ObjectDownload(Uri DownloadUri, DateTimeOffset ExpiresAt);
