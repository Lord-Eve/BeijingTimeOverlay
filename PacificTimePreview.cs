using System.Globalization;

namespace BeijingTimeOverlay;

internal static class HoverInput
{
    private static readonly int[] MouseKeys = { 1, 2, 4, 5, 6 };

    public static bool AnyMouseButtonDown(Func<int, short> readAsyncKeyState)
    {
        foreach (var key in MouseKeys)
        {
            // Only the high bit means currently held. The low (historical)
            // bit is unreliable and must not suppress background hovering.
            if ((readAsyncKeyState(key) & 0x8000) != 0) return true;
        }
        return false;
    }
}

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

internal readonly record struct PacificTimeText(string Time, string Date, string Zone, string Weekday, string BeijingWeekday)
{
    private static readonly CultureInfo WeekdayCulture = CultureInfo.GetCultureInfo("zh-CN");

    public string WeekdayComparison => $"{Weekday}（北京时间：{BeijingWeekday}）";

    public static PacificTimeText FromUtc(DateTimeOffset utc, TimeZoneInfo zone, TimeZoneInfo beijingZone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var beijing = TimeZoneInfo.ConvertTime(utc, beijingZone);
        return new PacificTimeText(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            local.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            zone.IsDaylightSavingTime(local) ? "PDT" : "PST",
            local.ToString("dddd", WeekdayCulture), beijing.ToString("dddd", WeekdayCulture));
    }
}
