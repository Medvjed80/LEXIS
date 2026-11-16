using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AleappReader.Core;

/// <summary>How LEAPP should read one input. A UFED/UFADE advanced logical zip is an iTunes backup for iLEAPP, with the
/// backup password taken from the .ufd (never placed on a command line).</summary>
public sealed record LeappInput(string Tool, string InputType, string? Password);

/// <summary>
/// Cellebrite UFED and UFADE advanced logical iOS extractions: a zip whose
/// <c>iPhoneDump/Backup Service/&lt;UDID&gt;/Snapshot/</c> folder is an iTunes backup, plus AFC media, app documents
/// and lockdown files. Detected from the zip alone; the .ufd beside it carries the password and the zip's SHA-256.
/// </summary>
public static class UfedLogical
{
    /// <summary>The Snapshot folder inside the zip: its entry prefix (forward slashes, ending in '/') and the UDID.</summary>
    public sealed record Snapshot(string Prefix, string Udid);

    /// <summary>The iTunes backup staged for <c>-t itunes</c>, and the SHA-256 of each file copied out of the zip.</summary>
    public sealed record StagedBackup(string Folder, string Udid, IReadOnlyList<(string Path, string Sha256)> Files);

    /// <summary>The Snapshot prefix that contains both Manifest.plist and Manifest.db, or null. No .ufd is required.</summary>
    public static Snapshot? Detect(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Detect(zip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public static Snapshot? Detect(ZipArchive zip)
    {
        var names = zip.Entries.Select(e => Norm(e.FullName)).Where(n => n != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var parts = n.Split('/');
            if (parts.Length < 4 || !parts[^1].Equals("Manifest.plist", StringComparison.OrdinalIgnoreCase)
                || !parts[^2].Equals("Snapshot", StringComparison.OrdinalIgnoreCase)
                || !parts[^4].Equals("Backup Service", StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = string.Join('/', parts[..^1]) + "/";
            if (names.Contains(prefix + "Manifest.db")) return new Snapshot(prefix, parts[^3]);
        }
        return null;
    }

    /// <summary>iLEAPP <c>itunes</c> and the UFD backup password when <paramref name="input"/> is this kind of zip;
    /// null for every other input (a plain zip, a filesystem1/ full file system, a folder).</summary>
    public static LeappInput? Choose(string input)
    {
        if (Detect(input) is null) return null;
        var pw = UfdInfo.ForExtraction(input)?.BackupPassword;
        return new LeappInput("ileapp", "itunes", string.IsNullOrEmpty(pw) ? null : pw);
    }

    /// <summary>Copies only the Snapshot members into a new folder under <paramref name="outputDir"/>, read-only, after
    /// the zip's SHA-256 has been checked against <paramref name="ufd"/> when it records one. A <c>..</c> or rooted
    /// member is rejected. The folder is an iTunes backup (Manifest.db at its root). The zip is not modified.</summary>
    public static async Task<StagedBackup> Stage(string zipPath, string outputDir, UfdInfo? ufd, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (ufd?.Sha256Of(Path.GetFileName(zipPath)) is { Length: > 0 })
        {
            progress?.Report("Checking the zip SHA-256 against the UFD…");
            var (computed, ok) = await UfdInfo.VerifyAsync(ufd, zipPath, ct);
            if (!ok) throw new InvalidDataException($"The zip SHA-256 does not match the UFD (computed {computed}).");
        }
        using var zip = ZipFile.OpenRead(zipPath);
        var snap = Detect(zip) ?? throw new InvalidDataException("Not a UFED advanced logical extraction: no Backup Service Snapshot.");
        var members = zip.Entries.Select(e => (Entry: e, Name: Norm(e.FullName)))
            .Where(x => x.Name.StartsWith(snap.Prefix, StringComparison.OrdinalIgnoreCase) && !x.Name.EndsWith('/')).ToList();
        var folder = Path.Combine(outputDir, "_lexis_itunes", snap.Udid + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var files = new List<(string, string)>();
        try
        {
            for (var i = 0; i < members.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (entry, name) = members[i];
                var rel = name[snap.Prefix.Length..];
                if (!Safe(rel, out var safe)) throw new InvalidDataException("The backup zip contains an unsafe path: " + name);
                var dest = Path.GetFullPath(Path.Combine(folder, safe.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The backup zip contains an unsafe path: " + name);
                progress?.Report($"Staging the iTunes backup: {i + 1}/{members.Count} {safe}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string sha;
                await using (var src = entry.Open())
                await using (var dst = File.Create(dest))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        hash.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    }
                    sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
                File.SetAttributes(dest, FileAttributes.ReadOnly);
                files.Add((safe, sha));
            }
        }
        catch
        {
            try { foreach (var f in files) File.SetAttributes(Path.Combine(folder, f.Item1), FileAttributes.Normal); Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return new StagedBackup(folder, snap.Udid, files);
    }

    /// <summary>The report's recorded input becomes the original zip, so the staged backup folder is not the evidence.</summary>
    public static void RememberEvidence(string manifestPath, string evidenceZip)
    {
        var node = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                   ?? throw new InvalidDataException("The LAVA manifest is not a JSON object.");
        node["param_input"] = Path.GetFullPath(evidenceZip);
        File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static string Norm(string name)
    {
        var n = name.Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal)) n = n[2..];
        return n.TrimStart('/');
    }

    static bool Safe(string rel, out string safe)
    {
        safe = "";
        var n = rel.Replace('\\', '/');
        if (n.StartsWith('/') || (n.Length >= 2 && n[1] == ':')) return false;
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is ".." or "." || p.Contains(':'))) return false;
        safe = string.Join('/', parts);
        return true;
    }
}
