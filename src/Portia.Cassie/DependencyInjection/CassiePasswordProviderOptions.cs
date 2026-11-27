namespace Cntryl.Portia;

/// <summary>Configures a Cassie projection data source with a password provider.</summary>
public sealed class CassiePasswordProviderOptions
{
    internal const string DefaultCheckpointTable = "portia_projection_checkpoints";

    /// <summary>Gets the native Cassie connection string without a Password entry.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Gets the callback that receives the host service provider and the open cancellation token.</summary>
    public required Func<IServiceProvider, CancellationToken, ValueTask<string>> PasswordProvider { get; init; }

    /// <summary>Gets the unqualified metadata table in the application's Cassie database.</summary>
    public string CheckpointTable { get; init; } = DefaultCheckpointTable;
}
