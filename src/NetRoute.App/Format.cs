namespace NetRoute.App;

/// <summary>Byte and speed formatting for people.</summary>
public static class Format
{
    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Bytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} B",
        < 1024 * 1024 => $"{bytes / 1024:0.0} KB",
        < 1024d * 1024 * 1024 => $"{bytes / 1024 / 1024:0.0} MB",
        _ => $"{bytes / 1024 / 1024 / 1024:0.00} GB"
    };

    /// <summary>"just now", "20 minutes ago", "yesterday at 21:04" — how a person would say it.</summary>
    public static string Ago(DateTimeOffset at)
    {
        var since = DateTimeOffset.Now - at.ToLocalTime();
        return since switch
        {
            { TotalMinutes: < 2 } => "just now",
            { TotalMinutes: < 60 } => $"{since.TotalMinutes:0} minutes ago",
            { TotalHours: < 24 } => $"{since.TotalHours:0} hours ago",
            { TotalDays: < 2 } => $"yesterday at {at.ToLocalTime():HH:mm}",
            _ => $"on {at.ToLocalTime():d MMMM}"
        };
    }
}
