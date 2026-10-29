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
            error = "URL is required";
            return false;
        }

        if (input.Length > MAX_LENGTH)
        {
            error = $"URL is too long, maximum length is {MAX_LENGTH}";
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? parsedUrl))
        {
            error = "URL must be valid and absolute";
            return false;
        }

        if (parsedUrl.AbsoluteUri.Length > MAX_LENGTH)
        {
            error = $"Encoded URL length is too long, maximum length is {MAX_LENGTH}";
            return false;
        }

        if (parsedUrl.Scheme != "http" && parsedUrl.Scheme != "https")
        {
            error = "URL scheme must be http or https";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUrl.UserInfo))
        {
            error = "URL must not contain credentials";
            return false;
        }

        if (parsedUrl.Host.Equals(ownHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "URL must not point to this service's own host";
            return false;
        }

        url = parsedUrl;
        return true;
    }
}
