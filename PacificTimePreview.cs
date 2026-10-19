using System.Globalization;

namespace BeijingTimeOverlay;

// No window or wall-clock dependency: hover timing uses a monotonic timestamp.
internal sealed class HoverPreviewState
{
    private long? _enteredAt;

    public bool Update(bool eligible, long now)
    {
        if (!eligible)
        {
            Reset();
            return false;
        }

        _enteredAt ??= now;
        return now - _enteredAt.Value >= 300;
    }

    public void Reset() => _enteredAt = null;
}

internal readonly record struct PacificTimeText(string Time, string Date, string Zone)
{
    public static PacificTimeText FromUtc(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return new PacificTimeText(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            local.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            zone.IsDaylightSavingTime(local) ? "PDT" : "PST");
    }
}
