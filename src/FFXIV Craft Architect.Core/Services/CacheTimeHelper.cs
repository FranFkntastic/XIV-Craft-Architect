using System.Globalization;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CacheTimeHelper
{
    public static DateTime NormalizeToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static DateTime ParseFetchedAt(object rawFetchedAt)
    {
        return rawFetchedAt switch
        {
            DateTime dt => NormalizeToUtc(dt),
            long unix when unix > 0 => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
            int unix when unix > 0 => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                => dto.UtcDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                => dt,
            _ => DateTime.UtcNow
        };
    }

    public static TimeSpan GetAge(DateTime fetchedAtUtc, DateTime? nowUtc = null)
    {
        var age = (nowUtc ?? DateTime.UtcNow) - NormalizeToUtc(fetchedAtUtc);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    public static bool IsStale(DateTime fetchedAtUtc, TimeSpan maxAge, DateTime? nowUtc = null)
    {
        return GetAge(fetchedAtUtc, nowUtc) > maxAge;
    }

    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours}h {(int)age.TotalMinutes % 60}m ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    public static string FormatAgeShort(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }
}
