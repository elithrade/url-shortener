using System.Net;
using System.Net.Sockets;

namespace Shortener;

public static class RateLimitKey
{
    public const string Unknown = "unknown";

    private const int Ipv6PrefixBytes = 8; // a /64 is the first 8 of 16 bytes

    public static string For(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, Ipv6PrefixBytes, bytes.Length - Ipv6PrefixBytes);

        // a new IPAddress also drops the scope id (fe80::1%3)
        return new IPAddress(bytes) + "/64";
    }
}
