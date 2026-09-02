using System.Security.Cryptography;
using System.Text;

namespace AlphaChannel.Plugin;

internal enum AlphaRole
{
    User,
    Patreon,
    Developer
}


internal static class UserRoles
{
    private static readonly HashSet<string> TrustedAccounts =
    [
        "26183EA7660F7762BF55555CB2D8C63871E5DA2B8A12B359787E492AD5634768",
        "6E885B857804F868C79C20C78F03696636427565A4FBAC95D7352D8530BFADF1",
        "43259212A529CCDD876FC12FE88BEB7E3E3410A5250985705A00AB64C21BCBA4",
    ];


    internal static AlphaRole GetRole(
        string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return AlphaRole.User;
        }


        var hash =
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        displayName
                            .Trim()
                            .ToLowerInvariant())));


        return TrustedAccounts.Contains(hash)
            ? AlphaRole.Developer
            : AlphaRole.User;
    }


    internal static bool IsDeveloper(
        string? displayName)
    {
        return GetRole(displayName)
            == AlphaRole.Developer;
    }
}