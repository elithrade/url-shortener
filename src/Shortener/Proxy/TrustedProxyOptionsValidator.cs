using System.Net;
using Microsoft.Extensions.Options;

namespace Shortener;

public sealed class TrustedProxyOptionsValidator : IValidateOptions<TrustedProxyOptions>
{
    private const string Key = "ForwardedHeaders";

    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!options.TrustAnyProxy && options.KnownNetworks.Length == 0)
        {
            return ValidateOptionsResult.Fail(
                $"{Key}:Enabled requires {Key}:TrustAnyProxy or at least one {Key}:KnownNetworks entry"
            );
        }

        if (options.KnownNetworks.Any(n => !IPNetwork.TryParse(n, out _)))
        {
            return ValidateOptionsResult.Fail(
                $"{Key}:KnownNetworks entries must be CIDR ranges such as 10.0.0.0/24"
            );
        }

        return ValidateOptionsResult.Success;
    }
}
