using System.Globalization;

namespace AleappReader.Core;

/// <summary>The gallery's filter by file size: a preset range, or a custom minimum and maximum in KB, MB or GB (1 KB =
/// 1,024 bytes). The lower bound is included and the upper bound left out, so a file of exactly 1 MB falls in "1 MB – 10 MB",
/// not in "100 KB – 1 MB". A file whose size is not known (not reachable, not in the extraction) is kept only when
/// <see cref="KeepUnknown"/> is on, or when no size filter is set.</summary>
public sealed record MediaSizeFilter(string Preset = MediaSizeFilter.Any, string? Min = null, string? Max = null, string Unit = "MB",
                                     bool KeepUnknown = false)
{
    public const string Any = "Any size", Under100K = "< 100 KB", K100To1M = "100 KB – 1 MB", M1To10 = "1 MB – 10 MB",
        M10To100 = "10 MB – 100 MB", Over100M = "> 100 MB", Custom = "Custom range", UnknownOnly = "Unknown size only";
    public static readonly string[] Presets = [Any, Under100K, K100To1M, M1To10, M10To100, Over100M, Custom, UnknownOnly];
    public static readonly string[] Units = ["KB", "MB", "GB"];

    const long KB = 1024, MB = KB * 1024, GB = MB * 1024;

    /// <summary>Bytes in one <paramref name="unit"/> (KB, MB or GB; anything else is MB).</summary>
    public static long UnitBytes(string unit) => unit.Trim().ToUpperInvariant() switch { "KB" => KB, "GB" => GB, _ => MB };

    /// <summary>A number typed in a size box ("1.5", "1,5", " 20 "), in bytes of <paramref name="unit"/>; null when blank,
    /// negative or not a number.</summary>
    public static long? Parse(string? text, string unit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || double.IsNaN(v) || double.IsInfinity(v)) return null;
        var bytes = v * UnitBytes(unit);
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Round(bytes);
    }

    /// <summary>The range in bytes: [Min, Max), either side open (null). Custom with nothing typed is no range.</summary>
    public (long? Min, long? Max) Range => Preset switch
    {
        Under100K => (null, 100 * KB),
        K100To1M => (100 * KB, MB),
        M1To10 => (MB, 10 * MB),
        M10To100 => (10 * MB, 100 * MB),
        Over100M => (100 * MB, null),
        Custom => (Parse(Min, Unit), Parse(Max, Unit) is { } hi ? hi + 1 : null), // a typed maximum is included
        _ => (null, null),
    };

    /// <summary>Whether the filter leaves anything out.</summary>
    public bool IsActive => Preset == UnknownOnly || Range is not (null, null);

    /// <summary>Whether a file of <paramref name="size"/> bytes passes; a negative size means unknown.</summary>
    public bool Matches(long size)
    {
        if (Preset == UnknownOnly) return size < 0;
        var (lo, hi) = Range;
        if (lo is null && hi is null) return true;
        if (size < 0) return KeepUnknown;
        return (lo is null || size >= lo) && (hi is null || size < hi);
    }

    /// <summary>A size for the tile's tooltip: "512 B", "1.4 MB"; "unknown size" for a negative one.</summary>
    public static string Format(long size) => size switch
    {
        < 0 => "unknown size",
        < KB => $"{size:N0} B",
        < MB => (size / (double)KB).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < GB => (size / (double)MB).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (size / (double)GB).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };
}
