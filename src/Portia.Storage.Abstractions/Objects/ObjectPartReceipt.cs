namespace Cntryl.Portia.Storage;

/// <summary>An opaque receipt supplied by the uploader after a part is accepted.</summary>
public sealed record ObjectPartReceipt(int PartNumber, string Token);
