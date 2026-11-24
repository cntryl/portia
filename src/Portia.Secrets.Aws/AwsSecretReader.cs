using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Cntryl.Portia.Secrets.Aws;

/// <summary>Reads current secret versions from AWS Secrets Manager.</summary>
public sealed class AwsSecretReader : ISecretReader
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    readonly IAmazonSecretsManager _client;

    /// <summary>Uses the supplied client. Its lifetime is owned by the caller.</summary>
    public AwsSecretReader(IAmazonSecretsManager client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async ValueTask<SecretValue> ReadCurrentAsync(string secretId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        cancellationToken.ThrowIfCancellationRequested();

        GetSecretValueResponse response;

        try
        {
            response = await _client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretId,
                VersionStage = "AWSCURRENT",
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException
                                      or HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SecretUnavailableException();
        }

        using var binary = response.SecretBinary;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versionId = response.VersionId;
            var text = response.SecretString;

            if (string.IsNullOrWhiteSpace(versionId) || (text is null) == (binary is null))
                throw new SecretUnavailableException();

            return new SecretValue(versionId, text is not null ? Utf8.GetBytes(text) : binary!.ToArray());
        }
        catch (EncoderFallbackException)
        {
            throw new SecretUnavailableException();
        }
        finally
        {
            if (binary is not null)
                ClearBinary(binary);
        }
    }

    static void ClearBinary(MemoryStream binary)
    {
        if (binary.TryGetBuffer(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            return;
        }

        if (!binary.CanWrite)
            return;

        // MemoryStream can have writable backing storage that TryGetBuffer cannot expose.
        Span<byte> zeros = stackalloc byte[1024];
        zeros.Clear();
        binary.Position = 0;

        while (binary.Position < binary.Length)
            binary.Write(zeros[..(int)Math.Min(zeros.Length, binary.Length - binary.Position)]);
    }
}
