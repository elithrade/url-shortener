using System.Security.Cryptography;

namespace Shortener;

public static class CodeGenerator
{
    private const string Characters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string GenerateCode(int length)
    {
        return RandomNumberGenerator.GetString(Characters, length);
    }
}
