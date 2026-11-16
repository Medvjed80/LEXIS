using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core;

/// <summary>A .ufd acquisition file (INI text written beside the extraction): case, device and acquisition
/// details and the SHA-256 recorded for each dump, all as stored.</summary>
public sealed class UfdInfo
{
    public string Path { get; }
    readonly List<(string Section, string Key, string Value)> _entries = [];

    UfdInfo(string path)
    {
        Path = path;
        var section = "";
        foreach (var raw in ReadText(path).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (line.IndexOf('=') is > 0 and var eq) _entries.Add((section, line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
    }

    /// <summary>UTF-8 when the file is valid UTF-8 (a BOM is accepted and not kept); otherwise Windows cp1252,
    /// which is how UFED and UFADE write the .ufd on Windows. Strict: a cp1252 é is not replaced with U+FFFD.</summary>
    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return utf8.GetString(bytes.AsSpan(3));
        try { return utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return DecodeCp1252(bytes); }
    }

    /// <summary>cp1252, including the bytes 0x80–0x9F that are not Latin-1. Undefined bytes stay as that code point.</summary>
    static string DecodeCp1252(byte[] bytes)
    {
        int[] high =
        [
            0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
            0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
            0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
            0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178,
        ];
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] < 0x80 ? (char)bytes[i] : bytes[i] < 0xA0 ? (char)high[bytes[i] - 0x80] : (char)bytes[i];
        return new string(chars);
    }

    public static UfdInfo Parse(string path) => new(path);

    public string Get(string section, string key) =>
        _entries.FirstOrDefault(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                                     e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";

    /// <summary><c>[General] ExtractionType</c> (AdvancedLogical, FullFileSystem, …).</summary>
    public string ExtractionKind => Get("General", "ExtractionType");
    /// <summary><c>[FileDump] ZIPLogicalPath</c>, the folder inside the zip that holds the dump (iPhoneDump).</summary>
    public string ZipLogicalPath => Get("FileDump", "ZIPLogicalPath");
    /// <summary><c>[General] BackupPassword</c>. UFADE's advanced logical extraction always uses 12345. Not shown in
    /// <see cref="Summary"/> and never written to a log.</summary>
    public string BackupPassword => Get("General", "BackupPassword");
    /// <summary><c>[General] GUID</c>, the device UDID.</summary>
    public string Udid => Get("General", "GUID");

    public IEnumerable<(string Key, string Value)> Section(string section) =>
        _entries.Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value));

    /// <summary>The extraction file this UFD describes, in the UFD's own folder. A FileDump or SHA-256 name that is
    /// not there falls back to the .zip with the UFD's own name, then to the only .zip beside it.</summary>
    public string? Extraction
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var name in new[] { Get("Dumps", "FileDump") }.Concat(Section("SHA256").Select(e => e.Key)))
            {
                if (name == "" || name.IndexOfAny(['/', '\\']) >= 0) continue;
                var full = System.IO.Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            var stem = System.IO.Path.ChangeExtension(Path, ".zip");
            if (File.Exists(stem)) return stem;
            try
            {
                var zips = Directory.GetFiles(dir, "*.zip");
                if (zips.Length == 1) return zips[0];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>The SHA-256 recorded for a dump file name, or "".</summary>
    public string Sha256Of(string fileName) => Get("SHA256", fileName);

    /// <summary>The .ufd beside an extraction file that names it (or the only .ufd there), or null.</summary>
    public static UfdInfo? ForExtraction(string? extraction)
    {
        if (string.IsNullOrWhiteSpace(extraction) || !File.Exists(extraction)) return null;
        try
        {
            var ufds = Directory.GetFiles(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(extraction))!, "*.ufd").Select(Parse).ToList();
            var name = System.IO.Path.GetFileName(extraction);
            return ufds.FirstOrDefault(u => u.Sha256Of(name) != "" || u.Get("Dumps", "FileDump").Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? (ufds.Count == 1 ? ufds[0] : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Recomputes the extraction's SHA-256 (read-only) and compares it with the one in the UFD.</summary>
    public static async Task<(string Computed, bool Matches)> VerifyAsync(UfdInfo ufd, string extraction, CancellationToken ct = default)
    {
        await using var f = new FileStream(extraction, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
        return (sha, sha.Equals(ufd.Sha256Of(System.IO.Path.GetFileName(extraction)), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The details worth showing, grouped as Device / Extraction / Case, labelled for a reader.</summary>
    public IReadOnlyList<(string Group, string Name, string Value)> Summary()
    {
        (string, string, string, string)[] map =
        [
            ("Device", "DeviceInfo", "Vendor", "Vendor"),
            ("Device", "General", "FullName", "Model"),
            ("Device", "DeviceInfo", "Model", "Model number"),
            ("Device", "DeviceInfo", "OS", "Operating system"),
            ("Device", "DeviceInfo", "SecurityPatchLevel", "Security patch"),
            ("Device", "DeviceInfo", "IMEI1", "IMEI 1"),
            ("Device", "DeviceInfo", "IMEI2", "IMEI 2"),
            ("Device", "DeviceInfo", "Chipset", "Chipset"),
            ("Extraction", "General", "ExtractionType", "Extraction type"),
            ("Extraction", "Image", "ExtractionMethod", "Method"),
            ("Extraction", "General", "Date", "Started"),
            ("Extraction", "General", "EndTime", "Ended"),
            ("Extraction", "ExtractionStatus", "ExtractionStatus", "Status"),
            ("Extraction", "General", "AcquisitionTool", "Tool"),
            ("Extraction", "General", "Version", "Tool version"),
            ("Extraction", "General", "ConnectionType", "Connection"),
            ("Extraction", "General", "UnitId", "Unit ID"),
        ];
        var list = map.Select(m => (m.Item1, m.Item4, Get(m.Item2, m.Item3))).Where(x => x.Item3 != "").ToList();
        list.AddRange(Section("SHA256").Select(e => ("Extraction", $"SHA-256 of {e.Key} (UFD)", e.Value)));
        list.Add(("Extraction", "UFD file", System.IO.Path.GetFileName(Path)));
        list.AddRange(Section("Crime Case").Where(e => e.Value != "").Select(e => ("Case", e.Key, e.Value)));
        return list;
    }
}
