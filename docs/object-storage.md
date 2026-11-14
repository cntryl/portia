# Object storage

`Cntryl.Portia.Storage.Abstractions` defines the vendor-neutral object contract. Application and domain assemblies can
reference this package without an AWS SDK dependency. Hosts that store data in Amazon S3 also reference
`Cntryl.Portia.Storage.Aws`.

The application supplies `IObjectStorageTenantResolver` and owns physical placement:

- `ResolveAsync` chooses the bucket/container and encryption reference. Buckets may be shared or dedicated.
- `GetObjectKey` chooses the stable final key for a tenant and digest. Portia imposes no directory or digest layout.
- `GetUploadKey` chooses a temporary key for the adapter-supplied upload identity. It must be unique to that upload
  and disjoint from final object keys. GCS stages its parts beneath this key.

The application is responsible for tenant isolation in that mapping. With a shared bucket, distinct tenant keys are
one option. Portia never creates buckets, containers, or naming policies. Register a configured S3 client and Data Protection:

```csharp
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IObjectStorageTenantResolver, ApplicationStorageTenantResolver>();
builder.Services.AddAwsObjectStorage(options => options.UseClient(s3Client));
```

For example, an application may use one shared bucket and organize its objects by tenant:

```csharp
sealed class ApplicationStorageTenantResolver : IObjectStorageTenantResolver
{
    public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
        ValueTask.FromResult(new ObjectStorageLocation("application-assets", "alias/application-assets"));

    public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
        $"documents/{Uri.EscapeDataString(tenantId.Value)}/{expected.Sha256}";

    public string GetUploadKey(TenantId tenantId, Uuid uploadId) =>
        $"pending/{Uri.EscapeDataString(tenantId.Value)}/{uploadId:N}";
}
```

Those names and paths are application choices. Final keys must remain stable for later reads and deletes; adapt
older layouts explicitly when migrating existing data. Upload sessions protect and retain the resolved bucket,
encryption reference, final key, and temporary key, so completion, replay, and abort use the original mapping even
if the resolver configuration changes. Existing resolver implementations must supply both key methods. Sessions
issued by an older adapter without a stored final key must be completed before upgrading the adapter. Default
registrations use the `UploadSession.v2` Data Protection purpose so older hosts cannot silently process new sessions
using their previous hardcoded layout.


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

`CompleteUploadAsync` reports rejected parts as `InvalidDataException`. Part receipts usually come from the uploading
client, so a bad receipt or an undersized part is an expected outcome. Both providers keep that session open, and the
caller can upload corrected parts and complete again. Content that fails length or SHA-256 verification also throws
`InvalidDataException`, and it ends the session.

The adapter uploads parts to a unique tenant-scoped staging key with SSE-KMS, streams the completed staged object to
check its actual length and full SHA-256, then conditionally copies verified bytes to
the application-resolved final key. It writes verified digest and length metadata during promotion and
removes staging data. A completion race verifies the already-promoted object before returning success. `HeadAndVerifyAsync`
checks length and adapter-written digest metadata without downloading object bytes. `OpenReadAsync` returns a stream after
that metadata check.

If multipart completion loses its response, retry `CompleteUploadAsync` with the same session and part receipts. A
completed staging object is streamed and verified again before promotion. The copy is conditional on both an absent
destination and the ETag of the staged object that was verified. A concurrent destination-copy conflict is retried a
bounded number of times. Unresolved completion or copy errors and canceled completion calls retain staging for a later
retry. Call `AbortUploadAsync` to intentionally discard a session and its staging data.

Staging cleanup after a decided outcome is best effort. If cleanup fails after promotion, `CompleteUploadAsync` still
reports success, and the lifecycle rules below remove the remaining staging data. If S3 fails the abort itself,
`AbortUploadAsync` throws that failure without removing staging. Retry the abort.

Configure S3 lifecycle rules to abort incomplete multipart uploads and expire completed objects under the
application's temporary-key namespace (for example `pending/` in the layout above). These rules protect against host loss and abandoned sessions. The library aborts and removes staging data for
explicit aborts, failed digest verification, and completed uploads. An unresolved retry leaves staged data in place so
that another completion request can recover it; the application's expiry rule eventually removes abandoned data.

The `StorageIntegration` test lane uses a digest-pinned Sqrzl image to exercise authenticated signed parts, multipart
completion, conditional writes and copies, shared and dedicated buckets, and separate API and worker registrations. Sqrzl surfaces
SSE-KMS headers and metadata but does not perform KMS encryption. The test client normalizes the SDK's URL-encoded
`CopySource` slashes before signing because this Sqrzl version parses literal slashes; the production adapter retains
the AWS SDK's encoded header. Locally, start `docker compose up -d sqrzl` and run the storage integration tests with
`PORTIA_S3_TEST_ENDPOINT=http://127.0.0.1:9000`, then stop the service with `docker compose down --volumes`.

## Azure Blob Storage

Hosts that store data in Azure Blob Storage reference `Cntryl.Portia.Storage.Azure` instead of the AWS package. The
adapter keeps the same contract, limits, session lifetime, and completion rules as the S3 adapter.

```csharp
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IObjectStorageTenantResolver, ApplicationStorageTenantResolver>();
builder.Services.AddAzureObjectStorage(options => options.UseClient(blobServiceClient));
```

The tenant resolver's two references mean:

- `BucketReference` is the application-chosen blob container, shared or dedicated. The application also chooses
  final and temporary blob keys through the same resolver. The adapter never creates or deletes containers.
- `EncryptionKeyReference` is the name of an Azure encryption scope in the same storage account. Create each scope with
  a customer-managed key in Key Vault (or Managed HSM) and keep it enabled. The adapter sends the scope on every write
  and pins it into every signed upload link, so a tenant's bytes cannot be written under another scope. If the scope is
  missing or disabled, Azure rejects the write and the adapter fails closed. Consider a container default scope with
  `DenyEncryptionScopeOverride` as a second guard.

Azure has no multipart-upload object. Each part is a block of one block blob at the application-resolved temporary key, and the
signed part URI already carries `comp=block&blockid=...`; the client sends the bytes with `PUT` and no extra headers. The
opaque part receipt is the `blockid` query value of the signed URI, exactly as it appears there. Azure returns no ETag
for a block, so the adapter validates the receipt against the block names it derives from the part number and checks the
uploaded blocks (including their sizes) with Get Block List. Because Azure enforces no minimum block size, the adapter
enforces `MinimumNonfinalPartLength` and the declared length itself before committing. Rejected receipts and undersized
parts throw `InvalidDataException` and leave the uncommitted blocks and the session open. Promotion uses Copy Blob From
URL, which is synchronous up to the 256 MiB object limit, with the verified staging ETag as the source condition and an
absent destination as the destination condition. The staging commit also requires an absent blob, so concurrent
completions cannot overwrite committed staging or change its ETag while another caller verifies and promotes it. `OpenReadAsync`
binds its download to the ETag whose metadata was verified; if the blob changes before download, it returns `null`
instead of streaming the unverified replacement.

Signed links are Service SAS tokens. A client built with a shared key credential signs locally. A client built with an
Entra ID credential (for example a managed identity) signs with a user delegation key, so its identity needs permission
to call Get User Delegation Key (for example the Storage Blob Delegator role) plus data access to the containers. A
client that can do neither, such as one built from a SAS token, makes signing throw `InvalidOperationException`. Part
links are write-only and download links are read-only; both are limited to 15 minutes, HTTPS only except for plain-HTTP
emulator endpoints.

Uncommitted blocks that nobody commits or aborts are garbage collected by Azure after about a week, and staging blobs
left by a host loss or an unresolved retry stay until removed. Add a blob lifecycle management rule that deletes blobs
under the application's temporary-key namespace (for example one day after last modification) in the configured
containers. Keep that lifecycle rule disjoint from final object keys. Explicit aborts, failed verification, and completed uploads remove staging data themselves, and
a cleanup failure after a decided outcome never changes that outcome.

Limitation: the container and scope are checked only when the first write reaches Azure, not at `CreateUploadAsync`,
because creating a block blob session needs no service call. The `StorageIntegration` lane has a shared conformance
test for Sqrzl's Azure Blob front door, using the same endpoint variable as S3. It is skipped until Sqrzl accepts an
encryption scope in a blob SAS and Copy Blob From URL (sqrzl-emulator issues 85 and 86). To run it against another
endpoint that supports encryption scopes, remove the skip and set `PORTIA_AZURE_BLOB_TEST_CONNECTION` to a shared-key
connection string and `PORTIA_AZURE_BLOB_TEST_ENCRYPTION_SCOPE` to an existing scope (default `portia-conformance`).

## Google Cloud Storage

Hosts that store data in Google Cloud Storage reference `Cntryl.Portia.Storage.Gcp` instead of the AWS package. The
adapter implements the same contract, limits, completion rules, and failure behavior described above.

```csharp
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IObjectStorageTenantResolver, ApplicationStorageTenantResolver>();
builder.Services.AddGcpObjectStorage(options => options.UseClient(storageClient));
```

The resolver's two references mean the following on Cloud Storage:

- `BucketReference` is the application-chosen bucket, shared or dedicated. Final keys and temporary upload roots
  come from the same resolver.
- `EncryptionKeyReference` is the full Cloud KMS key resource name,
  `projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}`. It is the customer-managed encryption
  key (CMEK) of every object the adapter writes.

Signed part URLs are plain `PUT` requests and cannot carry an encryption header, so Cloud Storage encrypts each staged
part with the bucket's default key. Set the resolved encryption key as that bucket's default. Tenants sharing a
bucket must resolve to that same default key; tenant separation then comes from application keys and authorization. The adapter fails
closed with `InvalidOperationException` when `CreateUploadAsync` finds a different default key, and again at completion
if any staged part or assembled object does not report the tenant key. Completion recovery also checks the key on an
existing destination before accepting a lost-response retry or a conditional-write conflict. Missing or mismatched
key evidence fails without deleting staging, preserving the unresolved upload for explicit correction and retry. Grant the Cloud Storage service agent
`cloudkms.cryptoKeyEncrypterDecrypter` on the key.

Cloud Storage has no S3-style multipart upload that the official .NET client can drive, so the adapter maps parts onto
objects. Each part is a separate staging object beneath the application-resolved upload key (`{uploadKey}/part-{n}`),
uploaded with a V4 signed `PUT`. The
part receipt is the object's generation, which Cloud Storage returns in the `x-goog-generation` response header of the
upload; pass that header value as `ObjectPartReceipt.Token`. At completion the adapter checks each receipt against the
staged part and enforces the 5 MiB nonfinal part rule and the declared length itself. It then reads the parts in order,
pinned to their receipt generations, once to verify the actual length and full SHA-256 and a second time to write them
to the application-resolved final key in one conditional upload (`ifGenerationMatch=0`, tenant key, digest and
length metadata). Overwriting a part invalidates its earlier receipt, so the bytes that were verified are the bytes that
are written. The adapter streams the object through the host twice instead of using server-side `compose`; that keeps
the writes inside the smallest API surface (including the Sqrzl emulator) at the cost of host bandwidth for objects up
to 256 MiB.

URL signing uses `StorageClient.CreateUrlSigner()`, so the client's credential must be a service-account key or a
credential that can call IAM `signBlob`, such as workload identity or an impersonated service account that holds
`iam.serviceAccounts.signBlob` on itself. Registration fails closed with `InvalidOperationException` when the credential
cannot sign. Pass a `UrlSigner` to `UseSigner` to override this. Signed links use the client's default
`https://storage.googleapis.com` host.

Configure a lifecycle rule on the application's temporary-key namespace after an appropriate interval (for example,
an `Age` condition with `matchesPrefix: ["pending/"]` for the sample layout above). The library removes staging data for explicit aborts, failed
verification, and completed uploads, and an unresolved retry leaves it for another attempt; the rule removes anything
abandoned. A signed `PUT` URL cannot limit the size of an uploaded part, so a holder of a URL can store more than the
declared length until the session is completed, aborted, or expires. Completion rejects such content.

The `StorageIntegration` lane also runs the shared conformance suite against Sqrzl's GCS front door. Start Sqrzl as
for S3 and set `PORTIA_S3_TEST_ENDPOINT=http://127.0.0.1:9000` (`PORTIA_GCS_TEST_ENDPOINT` overrides it). Sqrzl authenticates the JSON API with the configured
secret as a bearer token and validates GOOG4 HMAC signed URLs for the XML API (`portia-test` and `portia-local-secret`);
the test sends the signed path to Sqrzl with the signed `Host: storage.googleapis.com`. Sqrzl does not model bucket
default encryption or report an object's Cloud KMS key, so that lane switches off the key-evidence checks through an
internal test-only property. Production registrations always keep them on, and the unit tests cover them against an
in-memory JSON API.
