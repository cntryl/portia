# Data protection

Portia protects long-lived values with authenticated encryption and creates keyed hashes for
short-lived verification and deterministic identifier lookup. Reference
`Cntryl.Portia.DataProtection.Abstractions` from code that consumes these services. The host
references `Cntryl.Portia.DataProtection` and registers exactly one key ring.

## Host registration

Add the shared services with `AddPortiaDataProtection()`. In AWS, reference
`Cntryl.Portia.DataProtection.Aws` and register `AddAwsDataProtectionKeyRing` with a Secrets Manager client,
a KMS client, the full secret and wrapping key ARNs, and a stable ring identifier. Use the same
ring for hosts that need to read each other's artifacts. Configure credential resolution through
the host's AWS SDK clients and keep SDK response-body logging disabled.

In Development on macOS or Linux, register `AddDevelopmentDataProtectionKeyRing(path)` with the
absolute path to a key file in a dedicated private directory outside the repository. Initialize it
explicitly with `DevelopmentDataProtectionKeyRingInitializer.Initialize` and a Development
`IHostEnvironment`. Keep the directory owner-only and point API and worker hosts at the same file.
Restart the hosts after rotation to load the updated file. Keep the local ring across restarts
and protect any backups of the file because it contains the local key bytes.

## Protect and verify

Use a stable purpose for each field or operation. Construct the associated data from expected
metadata, such as a length-framed tenant, record and field ID. Reconstruct it from that metadata
when reading. For verification values, bind the challenge and owner instead. Apply expiry,
attempt limits, and single-use checks in the application. Binding a trusted record version into
the associated data also allows an application to reject replay of an earlier value at the same
location.

```csharp
var payload = await protector.ProtectAsync(value, "customer.tax-id", context, cancellationToken);
using var opened = await protector.UnprotectAsync(
    payload, "customer.tax-id", context, cancellationToken);
UseValue(opened.Plaintext);
```

Dispose the result as soon as the caller finishes with its plaintext. Clear caller-owned input
and any caller-created plaintext copies after use. Persist the protected bytes or encode them as
Base64 for a text store. Treat malformed, unsupported, and unknown-key artifacts as invalid.
A `DataProtectionKeyUnavailableException` indicates a provider failure. Preserve the pending
operation and handle or retry that failure separately from a verification mismatch.

## Operate the AWS ring

Create one symmetric KMS wrapping key and one Secrets Manager secret per app environment.
A provisioning identity generates each independent 32-byte root with
`GenerateDataKeyWithoutPlaintext` and stores its ciphertext in the secret. Configure KMS encryption
context with the exact `portia:ring`, `portia:key`, and `portia:role` values. Use opaque,
non-sensitive identifiers because the context can appear in CloudTrail.

The secret's current JSON version contains `version` (1), `ringId`, a positive increasing
`revision`, `activeKeyId`, and `keys`. Each key has `keyId`, `role` (`protection` or
`index`), and `wrappedRoot` (Base64 KMS ciphertext). The active entry must have the protection
role. Give every new root a new Portia ID. An index root is provisioned independently and pinned
by ID in application configuration.

Give the runtime `secretsmanager:GetSecretValue` on that secret and `kms:Decrypt` on that
wrapping key, restricted to its ring context. Reserve provisioning and secret updates for
the provisioning identity. Stage a candidate secret version with a custom stage, then move
`AWSCURRENT` with `UpdateSecretVersionStage` and the expected old version ID in
`RemoveFromVersionId`. If the move loses to a concurrent publisher, read the current version
before retrying the change. Check the resulting active ID and revision after each publication.

Each host locally caches unwrapped roots for a bounded lifetime and derives subkeys on use.
Encryption, decryption and hashing use those local keys. Size KMS traffic by the number of
processes, roots accessed, and refreshes. Choose the cache lifetime based on how long a process
may continue using a key it has already unwrapped after KMS access is revoked.

## Rotate and retire

1. Publish the new protection root alongside the current active root. Confirm every writer
   and reader that serves this data can observe the updated ring before switching the active ID.
2. Activate the new root in a later revision. Drain writers using the prior active root, including
   writes that obtained an old lease and may commit after activation.
3. Re-protect old values. `NeedsRotation` describes the snapshot that authenticated the value.
   Commit the replacement conditionally against the same record version you read. If the write
   loses to a concurrent update, reread and decide again. Use committed updates and a rescan
   as migration evidence.
4. Retire the old protection root after resolving remaining values and verification artifacts.
   Let old verification values expire or reissue them. Verify recovery of archives and backups
   that still reference old roots before retirement. Keep each derived AES key below the
   random-nonce invocation limit of 2^32 writes across all writers, including retries and
   re-protection.

For immutable identifier history, pin an independently generated index key using
`ComputeIndexHashAsync`. Canonicalize identifiers consistently and use a stable lookup scope.
In an emergency, publish a fresh index key, write new entries under its ID, and compute lookups
with both IDs during the overlap. Backfill an authoritative lookup from canonical values to
stable owner IDs, including any durable information required to rebuild that lookup later.
An old digest alone cannot produce the new digest. Keep the old index key while any required
lookups still rely on it. If the lookup enforces uniqueness, coordinate old and new candidates
inside the application's authoritative write boundary during the transition.

The artifact format uses Portia key IDs. A move to another wrapping provider requires securely
rewrapping all required roots, including pinned index roots, with their existing Portia IDs.
A cloud wrapping-key rotation and a Portia protection-root rotation have separate lifecycles.

## Artifact format

The v1 binary header contains the ASCII bytes `PDP`, the version byte `1`, the artifact kind
(`1` for ciphertext, `2` for verification, `3` for an index), an unsigned key ID length byte,
and the ASCII key ID. Key IDs contain 1–64 letters, digits, underscores, or hyphens.

For AES-256-GCM, the body contains a random 12-byte nonce, the ciphertext, and a 16-byte tag.
The associated data is the header, the context length as a big-endian 32-bit integer, and
the caller's context. Hash artifacts contain a 32-byte HMAC-SHA256 digest over that same framing
followed by the value. Keep the whole artifact, including its header, when storing hashes.

The 32-byte subkey is HKDF-SHA256 over the selected root with an empty salt. Its info contains
ASCII `Portia.DataProtection`, the version and kind bytes, the purpose byte count as a
big-endian 16-bit integer, and the exact UTF-8 purpose bytes. Values may contain up to 1 MiB.
Purpose and context are required and bounded at 256 UTF-8 bytes and 4 KiB, respectively.
