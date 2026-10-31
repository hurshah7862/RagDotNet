using System.Globalization;

namespace RagDotNet.Generation;

internal static class PageLocation
{
    /// <summary>Formats "p. 3" or "pp. 3–4" (en dash).</summary>
    public static string Format(int start, int? end) =>
        end is { } e && e != start
            ? string.Create(CultureInfo.InvariantCulture, $"pp. {start}–{e}")
            : string.Create(CultureInfo.InvariantCulture, $"p. {start}");
}
