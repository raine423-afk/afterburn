using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Afterburn: decides whether an address is on the public internet, as opposed to this PC or a
/// private/local network.
/// </summary>
public static class PublicWebAddress
{
    /// <summary>
    /// True when the address is http(s) and its host doesn't name this machine or a private/local network.
    /// Host names are checked by name here; the address they resolve to is checked when connecting.
    /// </summary>
    public static bool IsPublicUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.');
        if (host.Length == 0) return false;

        if (IPAddress.TryParse(host, out var ip)) return IsPublicIp(ip);

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
            return false;

        return true;
    }

    /// <summary>
    /// True when the IP address is a public internet address.
    /// </summary>
    public static bool IsPublicIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || b[0] >= 224);
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var bytes = ip.GetAddressBytes();
        // NAT64 / 6to4 addresses carry an IPv4 address inside; judge that one.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
            return IsPublicIp(new IPAddress(bytes.AsSpan(12, 4)));
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPublicIp(new IPAddress(bytes.AsSpan(2, 4)));

        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast
                 || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any));
    }
}

/// <summary>
/// Afterburn: an <see cref="HttpClient"/> for links written by third parties (e.g. a collection's external
/// downloads). It only talks to public internet addresses: every redirect is checked before it is followed,
/// and the address a host name resolves to is checked again right before connecting, so neither a redirect
/// nor a DNS trick can point it at this PC or the local network.
/// </summary>
public sealed class PublicWebHttpClient
{
    /// <summary>
    /// The client.
    /// </summary>
    public HttpClient Client { get; }

    internal PublicWebHttpClient(HttpClient client) => Client = client;

    internal static SocketsHttpHandler CreateSocketsHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        // When the system sends traffic through a proxy, this connection goes to the proxy itself,
        // which is allowed to be on this PC or the local network. The target was checked by name already.
        var allowed = IsConnectionToSystemProxy(context)
            ? addresses
            : addresses.Where(PublicWebAddress.IsPublicIp).ToArray();

        if (allowed.Length == 0)
            throw new HttpRequestException($"Blocked a connection to `{host}`: it isn't a public internet address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsConnectionToSystemProxy(SocketsHttpConnectionContext context)
    {
        try
        {
            var target = context.InitialRequestMessage.RequestUri;
            if (target is null) return false;

            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(target)) return false;

            var proxyUri = proxy.GetProxy(target);
            if (proxyUri is null || proxyUri == target) return false;

            return string.Equals(proxyUri.IdnHost.Trim('[', ']'), context.DnsEndPoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                   && proxyUri.Port == context.DnsEndPoint.Port;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Afterburn: follows redirects by hand, refusing any hop that leaves the public internet or drops from https to http.
/// </summary>
internal sealed class PublicRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!PublicWebAddress.IsPublicUri(request.RequestUri))
            throw new HttpRequestException($"Blocked a request to `{request.RequestUri}`: it isn't a public internet address.");

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var current = request;
        for (var hop = 0; IsRedirect(response.StatusCode); hop++)
        {
            var location = response.Headers.Location;
            if (location is null) return response;

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"Too many redirects starting from `{request.RequestUri}`.");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (!PublicWebAddress.IsPublicUri(next))
            {
                response.Dispose();
                throw new HttpRequestException($"Blocked a redirect to `{next}`: it isn't a public internet address.");
            }

            if (current.RequestUri!.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
            {
                response.Dispose();
                throw new HttpRequestException($"Blocked a redirect from https to `{next}`.");
            }

            var method = response.StatusCode == HttpStatusCode.SeeOther
                         || (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found && current.Method == HttpMethod.Post)
                ? HttpMethod.Get
                : current.Method;

            var nextRequest = new HttpRequestMessage(method, next)
            {
                Version = current.Version,
                VersionPolicy = current.VersionPolicy,
            };
            CopyHeaders(current.Headers, nextRequest.Headers, sameHost: string.Equals(current.RequestUri.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase));

            response.Dispose();
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = nextRequest;

            response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.Moved
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static void CopyHeaders(HttpRequestHeaders from, HttpRequestHeaders to, bool sameHost)
    {
        foreach (var (name, values) in from)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            // Never pass credentials on to a different site.
            if (!sameHost && (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                continue;
            to.TryAddWithoutValidation(name, values);
        }
    }
}
