using System.Globalization;

namespace Cntryl.Portia;

/// <summary>
///     A permission string is a stable identifier an application looks up in its own policy store.
///     Interpolating a request property into one must therefore produce the same text on every host,
///     regardless of the server's locale.
/// </summary>
public sealed class PermissionCultureTests
{
    /// <summary>
    ///     Verifies a generated <see cref="RequiresPermissionAttribute" /> token renders invariantly.
    ///     Swedish formats a negative integer with U+2212 MINUS SIGN rather than an ASCII hyphen, so a
    ///     culture-sensitive interpolation silently asks the evaluator about a different permission.
    /// </summary>
    [Fact]
    public async Task ShouldInterpolatePermissionTokensIndependentlyOfTheCurrentCulture()
    {
        var evaluator = TestPermissionEvaluator.AllowAll();
        using var busHost = TestRequestBus.Create(permissionEvaluator: evaluator);
        var original = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            _ = await busHost.Bus.SendAsync(new GetOrder(-42), RequestActor.System);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(["orders:-42:read"], evaluator.EvaluatedPermissions);
    }
}
