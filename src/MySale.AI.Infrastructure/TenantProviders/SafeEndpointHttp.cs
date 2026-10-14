using System.Net;
using System.Net.Sockets;
using MySale.AI.Application.TenantProviders;

namespace MySale.AI.Infrastructure.TenantProviders;

/// <summary>
/// HTTP client for company-supplied endpoints. Defends against SSRF: every connection resolves the host itself and
/// refuses private / loopback / link-local / metadata addresses (unless TenantAiProviders:AllowPrivateEndpoints), and
/// connects to the vetted address (no DNS-rebinding window). Redirects are never followed, so credentials are only
/// ever sent to the configured provider host. No request or header logging happens here.
/// </summary>
public static class SafeEndpointHttp
{
    public const string ClientName = "ai-tenant";

    public static SocketsHttpHandler CreateHandler(TenantAiProviderOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false, // a proxy would hide the real target from the address check
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) throw new HttpRequestException("The provider host could not be resolved.");
            if (!options.AllowPrivateEndpoints && addresses.Any(a => !TenantProviderValidation.IsPublic(a)))
                throw new HttpRequestException("The provider endpoint resolves to a private or internal network address, which is not allowed.");

            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    last = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw new HttpRequestException("The provider endpoint could not be reached.", last);
        }
    };
}
