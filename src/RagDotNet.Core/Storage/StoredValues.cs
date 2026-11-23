using System.Globalization;

namespace RagDotNet.Storage;

/// <summary>
/// Metadata values as stored. Some connectors (Redis) index only strings, string arrays and numbers, so a bool is stored
/// as an <c>int</c> (0 or 1) and a date as a <c>long</c>: microseconds since the Unix epoch, UTC. Other value types are
/// stored nullable, because a document may omit the field. Core converts here, at the store boundary only: callers,
/// filters and the keyword index keep the declared types.
/// </summary>
internal static class StoredValues
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>The field's type in the store.</summary>
    public static Type StoreType(Type declared)
    {
        if (declared == typeof(bool)) return typeof(int?);
        if (declared == typeof(DateTimeOffset)) return typeof(long?);
        return declared.IsValueType && Nullable.GetUnderlyingType(declared) is null ? typeof(Nullable<>).MakeGenericType(declared) : declared;
    }

    /// <summary>A declared value as stored.</summary>
    public static object? ToStore(object? value, Type declared) => value switch
    {
        bool b when declared == typeof(bool) => b ? 1 : 0,
        DateTimeOffset d when declared == typeof(DateTimeOffset) => UnixMicroseconds(d),
        _ => value,
    };

    /// <summary>A stored value as its declared type; accepts whichever numeric type a connector returns.</summary>
    public static object? FromStore(object? stored, Type declared)
    {
        if (stored is null) return null;
        if (declared == typeof(bool)) return Convert.ToInt64(stored, CultureInfo.InvariantCulture) != 0;
        if (declared == typeof(DateTimeOffset))
            return DateTimeOffset.UnixEpoch.AddTicks(Convert.ToInt64(stored, CultureInfo.InvariantCulture) * TicksPerMicrosecond);
        if (declared == typeof(int)) return Convert.ToInt32(stored, CultureInfo.InvariantCulture);
        if (declared == typeof(long)) return Convert.ToInt64(stored, CultureInfo.InvariantCulture);
        return stored;
    }

    // Floor division, so a date before 1970 with a sub-microsecond part still rounds down, like a later one.
    private static long UnixMicroseconds(DateTimeOffset d)
    {
        var ticks = d.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var micros = Math.DivRem(ticks, TicksPerMicrosecond, out var rest);
        return rest < 0 ? micros - 1 : micros;
    }
}
