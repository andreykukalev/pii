using System.Globalization;

namespace Pii.Worker;

public static class DailySchedule
{
    public static string? DueKey(DateTimeOffset now, int version = 1)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Vilnius"));
        return local.Hour >= 8 ? $"daily:vilnius:{local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:v{version}" : null;
    }
}