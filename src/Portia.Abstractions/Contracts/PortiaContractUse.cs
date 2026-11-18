namespace Cntryl.Portia;

/// <summary>Identifies the JSON contract directions for a configured example.</summary>
[Flags]
public enum PortiaContractUse
{
    /// <summary>No direction; not valid for an example registration.</summary>
    None = 0,

    /// <summary>Input accepted by a request contract.</summary>
    Input = 1,

    /// <summary>Output produced by a result contract.</summary>
    Output = 2,

    /// <summary>Both input and output.</summary>
    Both = Input | Output
}
