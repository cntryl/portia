using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Cntryl.Portia.Secrets;
using Cntryl.Portia.Secrets.Aws;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Portia.Tests.Secrets;

/// <summary>Exercises the AWS adapter through the shared secrets contract.</summary>
public sealed class AwsSecretReaderTests
{
    const string FirstVersion = "00000000000000000000000000000001";
    const string SecondVersion = "00000000000000000000000000000002";

    /// <summary>Resolves the host's reader and observes the current version on each read.</summary>
    [Fact]
    public async Task ShouldReadCurrentVersionGivenRotation()
    {
        // Arrange
        var response = new GetSecretValueResponse { VersionId = FirstVersion, SecretString = "text é" };
        using var client = new StubSecretsManager((_, _) => Task.FromResult(response));
        using var services = new ServiceCollection().AddAwsSecretsManager(client).BuildServiceProvider();
        var reader = services.GetRequiredService<ISecretReader>();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string secretId = "service/integration";
        using var first = await reader.ReadCurrentAsync(secretId, cancellationToken);
        response = new GetSecretValueResponse { VersionId = SecondVersion, SecretString = "updated" };

        // Act
        using var second = await reader.ReadCurrentAsync(secretId, cancellationToken);

        // Assert
        Assert.Equal(secretId, client.Request!.SecretId);
        Assert.Equal("AWSCURRENT", client.Request.VersionStage);
        Assert.Null(client.Request.VersionId);
        Assert.Equal(cancellationToken, client.CancellationToken);
        Assert.Equal(2, client.Calls);
        Assert.Equal(FirstVersion, first.VersionId);
        Assert.True(first.Bytes.Span.SequenceEqual("text é"u8));
        Assert.Equal(SecondVersion, second.VersionId);
        Assert.True(second.Bytes.Span.SequenceEqual("updated"u8));
    }

    /// <summary>Copies the AWS binary payload and clears the source buffer.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldReadAndClearGivenBinarySecret(bool publiclyVisible)
    {
        // Arrange
        byte[] source = [42, 1, 2, 3, 42];
        using var binary = new MemoryStream(source, 1, 3, writable: true, publiclyVisible);
        binary.Position = 1;
        using var client = new StubSecretsManager((_, _) => Task.FromResult(
            new GetSecretValueResponse { VersionId = FirstVersion, SecretBinary = binary }));
        var reader = new AwsSecretReader(client);

        // Act
        using var secret = await reader.ReadCurrentAsync("binary-secret", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FirstVersion, secret.VersionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, secret.Bytes.ToArray());
        Assert.Equal(new byte[] { 42, 0, 0, 0, 42 }, source);
    }

    /// <summary>Clears views already issued to the caller when the value is disposed.</summary>
    [Fact]
    public void ShouldClearOwnedBytesGivenDisposal()
    {
        // Arrange
        using var secret = new SecretValue("version", [4, 5, 6]);
        var view = secret.Bytes;

        // Act
        secret.Dispose();

        // Assert
        Assert.Equal(new byte[3], view.ToArray());
    }

    /// <summary>Rejects access to the payload after the caller has disposed it.</summary>
    [Fact]
    public void ShouldRejectByteAccessGivenDisposal()
    {
        // Arrange
        using var secret = new SecretValue("version", [4, 5, 6]);
        secret.Dispose();

        // Act
        var exception = Record.Exception(() => { _ = secret.Bytes; });

        // Assert
        Assert.IsType<ObjectDisposedException>(exception);
    }

    /// <summary>Fails closed for incomplete or ambiguous service replies.</summary>
    [Theory]
    [InlineData(null, "value", false)]
    [InlineData(" ", "value", false)]
    [InlineData(FirstVersion, null, false)]
    [InlineData(FirstVersion, "value", true)]
    public async Task ShouldRejectGivenInvalidServiceResponse(string? version, string? text, bool includeBinary)
    {
        // Arrange
        byte[] source = [1];
        using var binary = includeBinary ? new MemoryStream(source) : null;
        using var client = new StubSecretsManager((_, _) => Task.FromResult(
            new GetSecretValueResponse { VersionId = version, SecretString = text, SecretBinary = binary }));
        var reader = new AwsSecretReader(client);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var secret = await reader.ReadCurrentAsync("service/integration", TestContext.Current.CancellationToken);
        });

        // Assert
        var failure = Assert.IsType<SecretUnavailableException>(exception);
        Assert.Null(failure.InnerException);
        Assert.Equal(includeBinary ? (byte)0 : (byte)1, source[0]);
    }

    /// <summary>Does not carry service diagnostics into application exceptions.</summary>
    [Fact]
    public async Task ShouldSanitizeGivenServiceFailure()
    {
        // Arrange
        const string providerDetails = "provider-private-details";
        using var client = new StubSecretsManager((_, _) =>
            Task.FromException<GetSecretValueResponse>(new ResourceNotFoundException(providerDetails)));
        var reader = new AwsSecretReader(client);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var secret = await reader.ReadCurrentAsync("service/integration", TestContext.Current.CancellationToken);
        });

        // Assert
        var failure = Assert.IsType<SecretUnavailableException>(exception);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(providerDetails, failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Clears a binary response regardless of whether the caller still needs it.</summary>
    [Fact]
    public async Task ShouldClearLateResponseGivenCallerCancellation()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        byte[] source = [42, 42];
        using var binary = new MemoryStream(source);
        using var client = new StubSecretsManager((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new GetSecretValueResponse { VersionId = FirstVersion, SecretBinary = binary });
        });
        var reader = new AwsSecretReader(client);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var secret = await reader.ReadCurrentAsync("service/integration", cancellation.Token);
        });

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(new byte[2], source);
    }

    sealed class StubSecretsManager(Func<GetSecretValueRequest, CancellationToken, Task<GetSecretValueResponse>> read)
        : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public GetSecretValueRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int Calls { get; private set; }

        public override Task<GetSecretValueResponse> GetSecretValueAsync(
            GetSecretValueRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Request = request;
            CancellationToken = cancellationToken;
            Calls++;
            return read(request, cancellationToken);
        }
    }
}
