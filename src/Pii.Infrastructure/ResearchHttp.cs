using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Pii.Domain;

namespace Pii.Infrastructure;

public sealed class SourceUnavailableException(string code) : Exception(code);

public static class ResearchHttp
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumCompanyFactsBytes = 8 * MaximumBytes;
    private static readonly HashSet<string> Hosts = ["data.sec.gov", "www.ecb.europa.eu", "api.twelvedata.com"];

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellation) =>
            {
                if (!Hosts.Contains(context.DnsEndPoint.Host) || context.DnsEndPoint.Port != 443) throw new SourceUnavailableException("DeniedHost");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address))) throw new SourceUnavailableException("DeniedNetworkAddress");
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses[0], 443, cancellation); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 192 && bytes[1] == 0) && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    public static async Task<byte[]> GetAsync(HttpClient client, Uri uri, string? userAgent, string? key, CancellationToken cancellation, int maximumBytes = MaximumBytes)
    {
        if (uri.Scheme != "https" || uri.Port != 443 || !Hosts.Contains(uri.Host) || uri.UserInfo.Length != 0)
            throw new SourceUnavailableException("DeniedEndpoint");
        const string companyFactsPrefix = "/api/xbrl/companyfacts/CIK";
        var companyFacts = uri.Host == "data.sec.gov" && uri.Query.Length == 0 && uri.AbsolutePath.StartsWith(companyFactsPrefix, StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) && uri.AbsolutePath.Length == companyFactsPrefix.Length + 15
            && uri.AbsolutePath[companyFactsPrefix.Length..^5].All(char.IsAsciiDigit);
        if (maximumBytes <= 0 || maximumBytes > MaximumCompanyFactsBytes || maximumBytes > MaximumBytes && !companyFacts)
            throw new SourceUnavailableException("ResponseLimitNotPermittedForEndpoint");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (userAgent is not null)
        {
            if (userAgent.Length > 256 || userAgent.Any(char.IsControl)) throw new SourceUnavailableException("InvalidIdentifiedUserAgent");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        if (key is not null) request.Headers.TryAddWithoutValidation("Authorization", "apikey " + key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode) throw new SourceUnavailableException($"SourceHttp{(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > maximumBytes) throw new SourceUnavailableException("ResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await BoundedBytes.ReadAsync(stream, maximumBytes, cancellation);
    }

    public static void ValidateInstrument(Instrument instrument)
    {
        if (!ValidToken(instrument.Symbol, 32, ".-_") || !ValidToken(instrument.Exchange, 4, "") || instrument.Exchange.Length != 4
            || !ValidToken(instrument.Currency, 3, "") || instrument.Currency.Length != 3
            || instrument.Id != $"{instrument.Symbol}:{instrument.Exchange}:{instrument.Currency}")
            throw new SourceUnavailableException("NonCanonicalInstrument");
    }

    private static bool ValidToken(string token, int maximum, string punctuation) => token.Length is > 0 && token.Length <= maximum
        && token.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' || punctuation.Contains(character));
    public static string Text(JsonElement element, string property) => element.GetProperty(property).GetString() ?? throw new SourceUnavailableException("MissingField");
    public static decimal Number(JsonElement element) => element.ValueKind == JsonValueKind.Number ? element.GetDecimal()
        : decimal.Parse(element.GetString() ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}