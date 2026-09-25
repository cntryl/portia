namespace Cntryl.Portia.Storage;

/// <summary>A signed upload part destination and its opaque completion receipt metadata.</summary>
public sealed record ObjectUploadPart(int PartNumber, Uri UploadUri, DateTimeOffset ExpiresAt);
