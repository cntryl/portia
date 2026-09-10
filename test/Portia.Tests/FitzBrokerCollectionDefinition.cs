namespace Cntryl.Portia;

/// <summary>
///     Prevents the shared broker fixture from running concurrently with another collection instance.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FitzBrokerCollectionDefinition : ICollectionFixture<FitzBrokerFixture>
{
    /// <summary>
    ///     Identifies the live Fitz integration-test collection.
    /// </summary>
    public const string Name = "Fitz broker integration";
}
