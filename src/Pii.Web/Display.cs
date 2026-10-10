using System.Globalization;
using Pii.Application;

namespace Pii.Web;

public static class Display
{
    public static string Number(decimal? value) => value?.ToString("N2", CultureInfo.InvariantCulture) ?? "Unavailable";
    public static string Quantity(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    public static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "Not finished";
    public static string Tone(decimal? value) => value is > 0 ? "positive" : value is < 0 ? "negative" : "";
    public static bool CanonicalId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);
    public static bool Active(AnalysisRun run) => run.Status is RunStatus.Pending or RunStatus.Running;
    public static string? Citation(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var address)
        && address.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(address.UserInfo)
        && address.IsDefaultPort && address.Query.Length == 0 && address.Fragment.Length == 0
        && !address.IsLoopback ? address.AbsoluteUri : null;
}