namespace Shortener;

public static class CodeGenerator
{
    private static readonly char[] _characters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    public static string GenerateCode(int length)
    {
        return string.Create(
            length,
            0,
            static (span, _) =>
            {
                for (int i = 0; i < span.Length; i++)
                    span[i] = _characters[Random.Shared.Next(_characters.Length)];
            }
        );
    }
}
