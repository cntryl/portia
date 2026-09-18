namespace Cntryl.Portia;

/// <summary>Distinguishes a produced result from <c>default</c>, which is neither outcome.</summary>
enum ResultState : byte
{
    /// <summary>The zero value: no outcome was ever produced.</summary>
    Uninitialized = 0,

    /// <summary>The request succeeded.</summary>
    Succeeded = 1,

    /// <summary>The request failed in a way its handler anticipated.</summary>
    Failed = 2
}
