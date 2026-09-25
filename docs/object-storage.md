# Object storage

`Cntryl.Portia.Storage.Abstractions` defines the vendor-neutral object contract. Application and domain assemblies can
reference this package without an AWS SDK dependency. Hosts that store data in Amazon S3 also reference
`Cntryl.Portia.Storage.Aws`.

The S3 adapter expects an `IObjectStorageTenantResolver` that maps each `TenantId` to a dedicated tenant bucket and an
opaque KMS key reference. Register a configured S3 client and Data Protection:

```csharp
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IObjectStorageTenantResolver, ApplicationStorageTenantResolver>();
builder.Services.AddAwsObjectStorage(options => options.UseClient(s3Client));
```

The Data Protection key ring protects opaque upload-session tokens. API instances that may sign, complete, or abort the
same sessions must share a persistent key ring. Keep the ring available for at least the maximum upload-session life
(24 hours); do not expose its storage credentials to application callers.

An application declares a lowercase SHA-256 digest and expected length before starting an upload. It asks for a signed
part URI, sends the bytes with HTTP `PUT`, and passes the returned opaque part receipt to `CompleteUploadAsync`. It may
request additional numbered part URIs for larger payloads. The adapter rejects objects above 256 MiB. Business
authorization remains in the consuming application; a signed download link is not an authorization decision.

For multipart uploads, each part except the final part must be at least 5 MiB
(`ObjectStorageLimits.MinimumNonfinalPartLength`). The final part may be smaller. Both the S3 adapter and the testing
fake follow this completion rule.

The adapter uploads parts to a unique tenant-scoped staging key with SSE-KMS, streams the completed staged object to
check its actual length and full SHA-256, then conditionally copies verified bytes to
`content/sha256/{first_two_hex}/{sha256_digest}`. It writes verified digest and length metadata during promotion and
removes staging data. A completion race verifies the already-promoted object before returning success. `HeadAndVerifyAsync`
checks length and adapter-written digest metadata without downloading object bytes. `OpenReadAsync` returns a stream after
that metadata check.

If multipart completion loses its response, retry `CompleteUploadAsync` with the same session and part receipts. A
completed staging object is streamed and verified again before promotion. The copy is conditional on both an absent
destination and the ETag of the staged object that was verified. A concurrent destination-copy conflict is retried a
bounded number of times. Unresolved completion or copy errors and canceled completion calls retain staging for a later
retry. Call `AbortUploadAsync` to intentionally discard a session and its staging data.

Configure S3 lifecycle rules to abort incomplete multipart uploads and expire completed objects under the `staging/`
prefix. These rules protect against host loss and abandoned sessions. The library aborts and removes staging data for
explicit aborts, failed digest verification, and completed uploads. An unresolved retry leaves staged data in place so
that another completion request can recover it; the `staging/` expiry rule eventually removes abandoned data.

The `StorageIntegration` test lane uses a digest-pinned Sqrzl image to exercise authenticated signed parts, multipart
completion, conditional writes and copies, tenant buckets, and separate API and worker registrations. Sqrzl surfaces
SSE-KMS headers and metadata but does not perform KMS encryption. The test client normalizes the SDK's URL-encoded
`CopySource` slashes before signing because this Sqrzl version parses literal slashes; the production adapter retains
the AWS SDK's encoded header. Locally, start `docker compose up -d sqrzl` and run the storage integration tests with
`PORTIA_S3_TEST_ENDPOINT=http://127.0.0.1:9000`, then stop the service with `docker compose down --volumes`.
