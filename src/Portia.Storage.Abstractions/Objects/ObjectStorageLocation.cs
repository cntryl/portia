namespace Cntryl.Portia.Storage;

/// <summary>Opaque per-tenant physical storage and encryption references.</summary>
public sealed record ObjectStorageLocation(string BucketReference, string EncryptionKeyReference);
