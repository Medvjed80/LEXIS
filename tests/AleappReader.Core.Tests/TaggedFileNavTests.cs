using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class TaggedFileNavTests
{
    [Fact]
    public void Navigation_resolves_the_device_path_and_the_parent_folder()
    {
        const string recorded = @"\Dump\data\media\0\DCIM\a.jpg";
        Assert.Equal("Dump/data/media/0/DCIM/a.jpg", TaggedFileNav.Rel(recorded));
        Assert.Equal("/data/media/0/DCIM/a.jpg", TaggedFileNav.Device(recorded));
        Assert.Equal("Dump/data/media/0/DCIM/a.jpg", TaggedFileNav.RevealRel(recorded));
        Assert.Equal("/data/media/0/DCIM", TaggedFileNav.FolderDevice(recorded));

        // already a device path (as after a portable case stores extraction-relative paths that DevicePath maps)
        Assert.Equal("/data/media/0/DCIM/a.jpg", TaggedFileNav.Device("data/media/0/DCIM/a.jpg"));
        Assert.Equal("/data/media/0/DCIM", TaggedFileNav.FolderDevice("data/media/0/DCIM/a.jpg"));
    }

    [Fact]
    public void Navigation_maps_an_archive_member_to_the_archive_in_the_extraction()
    {
        var inner = "Dump/sdcard/x.zip" + ExtractionFiles.Inside + "in.txt";
        Assert.Equal("Dump/sdcard/x.zip!/in.txt", TaggedFileNav.Rel("/" + inner.Replace('/', '\\')));
        Assert.Equal("/sdcard/x.zip!/in.txt", TaggedFileNav.Device(inner));
        Assert.Equal("Dump/sdcard/x.zip", TaggedFileNav.RevealRel(inner));
        Assert.Equal("/sdcard", TaggedFileNav.FolderDevice(inner));
    }

    [Fact]
    public void Editing_or_removing_a_file_tag_refreshes_the_listed_items()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-tagged-nav-");
        try
        {
            var tags = new TagStore(dir.FullName);
            tags.WriteFileTranscript("Dump/data/media/0/DCIM/a.jpg", "auto only");
            Assert.Empty(tags.All()); // auto transcript is not a tagged item

            TaggedFileNav.SetFile(tags, @"\Dump\data\media\0\DCIM\a.jpg", "Evidence", "check the date");
            var listed = tags.All();
            Assert.Single(listed);
            Assert.Equal("Evidence", listed[0].Tag);
            Assert.Equal("check the date", listed[0].Note);
            Assert.Equal("Dump/data/media/0/DCIM/a.jpg", TagStore.FileKey(listed[0].TableName[TagStore.FilePrefix.Length..]));

            TaggedFileNav.SetFile(tags, "Dump/data/media/0/DCIM/a.jpg", "Important", "revised note");
            listed = tags.All();
            Assert.Single(listed);
            Assert.Equal("Important", listed[0].Tag);
            Assert.Equal("revised note", listed[0].Note);

            tags.SetFiles(["Dump/sdcard/b.bin", "Dump/sdcard/c.bin"], "Follow up");
            Assert.Equal(3, tags.All().Count);

            TaggedFileNav.RemoveFiles(tags, ["Dump/data/media/0/DCIM/a.jpg", "Dump/sdcard/b.bin"]);
            listed = tags.All();
            Assert.Single(listed);
            Assert.Equal("Dump/sdcard/c.bin", TagStore.FileKey(listed[0].TableName[TagStore.FilePrefix.Length..]));
            Assert.DoesNotContain(tags.FileTags().Keys, k => k.Contains("a.jpg", StringComparison.OrdinalIgnoreCase) || k.Contains("b.bin", StringComparison.Ordinal));
        }
        finally { dir.Delete(true); }
    }
}
