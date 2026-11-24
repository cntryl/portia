# Secrets

Reference `Cntryl.Portia.Secrets.Abstractions` in code that reads application secrets at runtime.
`ISecretReader.ReadCurrentAsync` returns the provider's current value as bytes with an opaque
version identifier. Compare version IDs for equality within one secret. Keep provider-specific
secret identifiers in host configuration.

## AWS Secrets Manager

Reference `Cntryl.Portia.Secrets.Aws` in the host and register a configured Secrets Manager client.
The host owns the client and its lifetime.

```csharp
using Cntryl.Portia.Secrets.Aws;

services.AddAwsSecretsManager(secretsManager);
```

The adapter calls `GetSecretValue` at `AWSCURRENT` for each read. It provides UTF-8 bytes for
`SecretString` and the decoded bytes for `SecretBinary`. Give the runtime
`secretsmanager:GetSecretValue` on the secrets it reads and `kms:Decrypt` on the protecting
KMS key when the secret uses a customer-managed key. Use provider administration tooling for
provisioning and version activation. [AWS GetSecretValue](https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html)

## Use a secret

Inject `ISecretReader` and dispose the value after the final use of its bytes.

```csharp
using var secret = await reader.ReadCurrentAsync(secretId, cancellationToken);
await SendWithCredentialAsync(secret.Bytes, cancellationToken);
```

A missing or unreadable current version raises `SecretUnavailableException`. Caller cancellation
raises `OperationCanceledException`. Disposal clears the owned bytes. Clear any copies you create
and keep values out of application logs and SDK response-body logging. The AWS SDK exposes
`SecretString` as a managed string, which cannot be cleared in place.

The [AWS data protection provider](data-protection.md#operate-the-aws-ring) uses the same adapter
to read its manifest with the configured client.
