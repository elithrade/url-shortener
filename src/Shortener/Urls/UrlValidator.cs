using System.Diagnostics.CodeAnalysis;

namespace Shortener;

public static class UrlValidator
{
    private const int MAX_LENGTH = 2048;

    public static bool TryValidate(
        string? input,
        string ownHost,
        [NotNullWhen(true)] out Uri? url,
        [NotNullWhen(false)] out string? error
    )
    {
        url = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Url is required";
            return false;
        }

        if (input.Length > MAX_LENGTH)
        {
            error = $"Url is too long, maximum length is {MAX_LENGTH}";
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? parsedUrl))
        {
            error = "Url must be valid and absolute";
            return false;
        }

        if (parsedUrl.Scheme != "http" && parsedUrl.Scheme != "https")
        {
            error = "Invalid url scheme, only http or https allowed";
            return false;
        }

        if (string.IsNullOrEmpty(parsedUrl.Host))
        {
            error = "Url must have a host";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUrl.UserInfo))
        {
            error = "Url must not contain credentials";
            return false;
        }

        if (parsedUrl.Host.Equals(ownHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "url host cannot be own host";
            return false;
        }

        url = parsedUrl;
        return true;
    }
}
