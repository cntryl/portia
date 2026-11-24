using System.Threading;
using System.Threading.Tasks;

namespace Cntryl.Portia.Secrets;

/// <summary>Reads runtime secrets from a configured provider.</summary>
public interface ISecretReader
{
    /// <summary>Reads the provider's current version of the specified secret.</summary>
    /// <param name="secretId">The provider's identifier for the secret.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The version and bytes of the secret. The caller owns and must dispose the value.</returns>
    /// <exception cref="SecretUnavailableException">The current value could not be obtained.</exception>
    ValueTask<SecretValue> ReadCurrentAsync(string secretId, CancellationToken cancellationToken = default);
}
