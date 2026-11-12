# Migrating to Portia 0.11.1

Portia 0.11.1 is a patch release on the .NET 11 and Fitz 4.1 baseline of 0.11.0. Keep the complete
`Cntryl.Portia.*` package family on 0.11.1. Applications upgrading from an earlier version must
first follow the [0.11.0 migration notes](migration-0.11.0.md).

## Dependencies

`Cntryl.Portia.Storage.Aws` now depends on AWSSDK.S3 4.1.0. Update a direct or central AWSSDK.S3
reference to at least 4.1.0 instead of suppressing the downgrade diagnostic. Fitz .NET stays on 4.1.0,
with the same Fitz server 0.4.0 / Wire 3 requirement as 0.11.0.

## MCP HTTP session mode

Portia's MCP Streamable HTTP host supports only the stateless session mode. The
`AddMcpHttp(options => ...)` callback remains available for other SDK transport settings. A callback
that leaves the final session mode stateful now fails validation during endpoint mapping or host
startup, where it previously ran.

Remove the stateful session setting from that callback. Portia will enable stateful modes only after
their cancellation path is qualified. See [Model Context Protocol](mcp.md) for the hosting options.

## Object storage completion failures

When S3 rejects the parts of a multipart upload, `IObjectStorage.CompleteUploadAsync` now throws
`InvalidDataException`. The affected S3 error codes are `InvalidPart`, `InvalidPartOrder` and
`EntityTooSmall`. Before 0.11.1 the S3 adapter let the `AmazonS3Exception` escape.

The session now stays open after this outcome. Part receipts usually come from the uploading client,
so treat it as an expected rejection: ask the client to upload corrected parts, then call
`CompleteUploadAsync` again with the same session. Content that fails length or SHA-256 verification
also throws `InvalidDataException`, but it ends the session.

Update code that caught `AmazonS3Exception` around `CompleteUploadAsync` for these cases. Code that
already handles `InvalidDataException` needs no change.

Two other outcomes changed:

- A completion that promoted the object reports success even if the staging cleanup after it fails.
  Keep the S3 lifecycle rules for the `staging/` prefix that remove leftover data.
- A failed `AbortUploadAsync` throws the abort's own error and leaves staging in place. Retry the
  abort to remove it.

`FakeObjectStorage` follows the same contract. Tests that expected a rejected receipt or an
undersized nonfinal part to end the session should now expect it to stay open.

## Application structure diagnostics

`PORTIA206` no longer reports a Core reference to an application project whose name ends in
`.Abstractions`, such as `{App}.Abstractions` or `{App}.Billing.Abstractions`. Remove any
suppressions you added for that reference. Common's dependency boundary is unchanged.

Common features may now keep `[PortiaJsonContext]` contexts in `Features/{Feature}/Serialization`.
Applications that kept per-feature contexts at Common's project root to satisfy `PORTIA203` can move
them there. The folder accepts only Portia JSON contexts.

A static `*DomainEventCatalog` adapter is now accepted in any feature's `Events` folder, not only in
a feature named `Platform`. No change is required.
