using System.Net;
using System.Net.Sockets;

namespace UrlShortener.Api;

/// <summary>Validates and normalizes target URLs. Blocks non-http(s) schemes, credentials and private hosts (SSRF / abuse).</summary>
public static class UrlValidator
{
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "url is required.";
            return false;
        }

        if (input.Length > LinkService.MaxUrlLength)
        {
            error = $"url must not exceed {LinkService.MaxUrlLength} characters.";
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "url must be an absolute http or https URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "url must not contain credentials.";
            return false;
        }

        if (IsPrivateHost(uri))
        {
            error = "url host is not allowed.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsPrivateHost(Uri uri)
    {
        var host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = ip.GetAddressBytes()[0];
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (first & 0xFE) == 0xFC;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0
            || b[0] == 10
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }
}
