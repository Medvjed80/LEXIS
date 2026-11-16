namespace AleappReader.Core;

/// <summary>How a tagged file is found in Extraction Files and how its tag is edited, so the Tagged Items view
/// and the portable reader share the same path rules (device path, archive members, slash variants).</summary>
public static class TaggedFileNav
{
    /// <summary>The extraction-relative path stored as the file's tag key, as Extraction Files lists it.</summary>
    public static string Rel(string taggedPath) => TagStore.FileKey(taggedPath);

    /// <summary>Where the file sits on the device, for the folder tree and for "Open in original folder".</summary>
    public static string Device(string taggedPath) => EvidenceOrigin.DevicePath(Rel(taggedPath));

    /// <summary>The extraction path Extraction Files should select: a file inside an archive is its archive
    /// (the archive is what a portable case carries, and what the tree lists until it is explored).</summary>
    public static string RevealRel(string taggedPath)
    {
        var rel = Rel(taggedPath);
        var cut = rel.IndexOf(ExtractionFiles.Inside, StringComparison.Ordinal);
        return cut > 0 ? rel[..cut] : rel;
    }

    /// <summary>The device-path folder that should be opened so the revealed file is visible in the list.</summary>
    public static string FolderDevice(string taggedPath)
    {
        var device = EvidenceOrigin.DevicePath(RevealRel(taggedPath));
        var cut = device.LastIndexOf('/');
        return cut > 0 ? device[..cut] : "";
    }

    /// <summary>Sets or changes a file's tag and optional note (slash variants are normalised like <see cref="TagStore.SetFiles"/>).
    /// A null note leaves the note it already has.</summary>
    public static void SetFile(TagStore tags, string path, string tag, string? note = null)
    {
        var key = TagStore.FileKey(path);
        if (key != path) tags.SetMany(TagStore.FilePrefix + path, [0], null);
        tags.Set(TagStore.FilePrefix + key, 0, string.IsNullOrWhiteSpace(tag) ? TagStore.DefaultTags[0] : tag.Trim(), note);
    }

    /// <summary>Removes the tags of the given files. A file with no tag (and no examiner note) leaves the tagged list.</summary>
    public static void RemoveFiles(TagStore tags, IEnumerable<string> paths) => tags.SetFiles(paths, null);
}
