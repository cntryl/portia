namespace Cntryl.Portia.Storage;

/// <summary>An opaque, provider-issued multipart upload handle.</summary>
public sealed record ObjectUploadSession(string Token);
