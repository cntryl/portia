namespace Cntryl.Portia;

// Framework identities are persisted as UTF-8 fields separated by NUL. Reject ambiguous text at
// construction so every accepted identity has distinct durable key bytes.
static class IdentityText
{
    public static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return IsValid(value)
            ? value
            : throw new ArgumentException(
                "An identity cannot contain NUL or malformed UTF-16 text.", parameterName);
    }

    public static bool IsValid(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '\0' || char.IsLowSurrogate(character))
                return false;

            if (char.IsHighSurrogate(character)
                && (++index == value.Length || !char.IsLowSurrogate(value[index])))
                return false;
        }

        return true;
    }
}
