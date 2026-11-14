namespace Cntryl.Portia.Storage;

/// <summary>Application-owned physical storage and encryption references. A bucket or container may be shared.</summary>
public sealed record ObjectStorageLocation(string BucketReference, string EncryptionKeyReference);
