using System;
using System.Globalization;

/// <summary>
/// Pure-string formatting helpers shared by the leaderboard scene and its
/// row builder. Extracted from <c>LeaderboardScreenController</c> so they
/// can be reused without dragging the controller's instance state along.
/// View-layer (uses <see cref="DateTime"/>) — fine because this is render-
/// formatting only, not domain logic.
/// </summary>
public static class LeaderboardFormatters
{
    /// <summary>
    /// Long form: "h:mm:ss.fff" / "m:ss.fff" / "s.fff". Used in row time
    /// labels on size-filtered tabs and in the player-rank panel.
    /// </summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        int totalMillis = (int)(seconds * 1000);
        int hours = totalMillis / 3600000;
        int mins = (totalMillis % 3600000) / 60000;
        int secs = (totalMillis % 60000) / 1000;
        int millis = totalMillis % 1000;

        if (hours > 0)
            return $"{hours}:{mins:D2}:{secs:D2}.{millis:D3}";
        if (mins > 0)
            return $"{mins}:{secs:D2}.{millis:D3}";
        return $"{secs}.{millis:D3}";
    }

    /// <summary>
    /// Compact time format for the All tab — drops millisecond precision.
    /// Under 1 minute: "45s". Under 1 hour: "12m 34s". Over 1 hour: "1h 23m".
    /// </summary>
    public static string FormatCompactTime(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        int totalSecs = (int)seconds;
        if (totalSecs < 60)
            return $"{totalSecs}s";
        int mins = totalSecs / 60;
        int secs = totalSecs % 60;
        if (mins < 60)
            return $"{mins}m {secs:D2}s";
        int hours = mins / 60;
        mins %= 60;
        return $"{hours}h {mins:D2}m";
    }

    /// <summary>
    /// Relative date label: "now", "5m ago", "2h ago", "3d ago", "4mo ago",
    /// "1yr ago". Driven off the current UTC time, so the result drifts
    /// across calls but that's the desired UX (a row recorded "10m ago"
    /// reads as "1h ago" later).
    /// </summary>
    public static string FormatRelativeDate(string iso8601)
    {
        if (string.IsNullOrEmpty(iso8601))
            return "";

        if (!DateTime.TryParse(iso8601, null, DateTimeStyles.RoundtripKind, out DateTime date))
            return "";

        var span = DateTime.UtcNow - date.ToUniversalTime();

        if (span.TotalMinutes < 1)
            return "now";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30)
            return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365)
            return $"{(int)(span.TotalDays / 30)}mo ago";

        return $"{(int)(span.TotalDays / 365)}yr ago";
    }

    /// <summary>
    /// Tooltip-friendly local-time stamp: "yyyy-MM-dd HH:mm:ss". Used as
    /// the hover-tooltip on relative date labels so the exact moment is
    /// reachable without losing the at-a-glance "5m ago" surface.
    /// </summary>
    public static string FormatExactDate(string iso8601)
    {
        if (string.IsNullOrEmpty(iso8601))
            return "";

        if (!DateTime.TryParse(iso8601, null, DateTimeStyles.RoundtripKind, out DateTime date))
            return "";

        return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
