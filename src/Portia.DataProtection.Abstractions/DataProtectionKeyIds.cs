namespace Cntryl.Portia.DataProtection;

static class DataProtectionKeyIds
{
    internal const int MaxLength = 64;

    internal static bool IsValid(string? id)
    {
        if (id is null || id.Length is < 1 or > MaxLength)
            return false;

        foreach (var character in id)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        }

        return true;
    }
}
