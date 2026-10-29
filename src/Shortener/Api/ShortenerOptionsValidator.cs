using Microsoft.Extensions.Options;

namespace Shortener;

public sealed class ShortenerOptionsValidator(IHostEnvironment env)
    : IValidateOptions<ShortenerOptions>
{
    private const string Key = "Shortener:BaseUrl";

    private readonly IHostEnvironment _env = env;

    public ValidateOptionsResult Validate(string? name, ShortenerOptions options)
    {
        var input = options.BaseUrl;

        if (string.IsNullOrWhiteSpace(input))
        {
            return ValidateOptionsResult.Fail($"{Key} is required");
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? parsedUrl))
        {
            return ValidateOptionsResult.Fail($"{Key} must be a valid absolute URL");
        }

        if (parsedUrl.Scheme != "http" && parsedUrl.Scheme != "https")
        {
            return ValidateOptionsResult.Fail($"{Key} must use http or https");
        }

        if (!string.IsNullOrEmpty(parsedUrl.UserInfo))
        {
            return ValidateOptionsResult.Fail($"{Key} must not contain credentials");
        }

        if (parsedUrl.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail($"{Key} must not contain a fragment");
        }

        if (!_env.IsDevelopment() && parsedUrl.Scheme == "http")
        {
            return ValidateOptionsResult.Fail($"{Key} must use https outside Development");
        }

        if (!string.IsNullOrEmpty(parsedUrl.Query))
        {
            return ValidateOptionsResult.Fail($"{Key} must not contain a query");
        }

        if (parsedUrl.AbsolutePath != "/")
        {
            return ValidateOptionsResult.Fail($"{Key} must not contain a path");
        }

        return ValidateOptionsResult.Success;
    }
}
