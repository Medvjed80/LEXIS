using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AleappReader.Core.Tests;

/// <summary>Synthetic UFED/UFADE advanced logical zips: no real device data.</summary>
public class UfedLogicalTests : IDisposable
{
    readonly SyntheticCase _c = new();
    const string Udid = "4c47b472778d23440f0c22389a532e6b6757fe5e";
    string Snap => $"iPhoneDump/Backup Service/{Udid}/Snapshot/";

    public void Dispose() => _c.Dispose();

    [Fact]
    public void A_cp1252_ufd_keeps_an_accented_name_and_finds_the_zip()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_c.Root, "ufd-cp")).FullName;
        var name = "Apple_iPhone été.zip";
        var zip = Path.Combine(dir, name);
        File.WriteAllBytes(zip, "zip"u8.ToArray());
        var sha = Convert.ToHexStringLower(SHA256.HashData("zip"u8.ToArray()));
        var text = "[DeviceInfo]\r\nVendor=Apple\r\n[Dumps]\r\nFileDump=" + name + "\r\n[FileDump]\r\nType=ZIPfolder\r\nZIPLogicalPath=iPhoneDump\r\n" +
                   "[General]\r\nExtractionType=AdvancedLogical\r\nFullName=Téléphone\r\nBackupPassword=12345\r\nGUID=" + Udid + "\r\nVendor=Apple\r\n" +
                   "[SHA256]\r\n" + name + "=" + sha + "\r\n";
        File.WriteAllBytes(Path.Combine(dir, "case.ufd"), Cp1252(text));

        var ufd = UfdInfo.Parse(Path.Combine(dir, "case.ufd"));
        Assert.Equal("Téléphone", ufd.Get("General", "FullName"));
        Assert.Equal(("AdvancedLogical", "iPhoneDump", "12345", Udid), (ufd.ExtractionKind, ufd.ZipLogicalPath, ufd.BackupPassword, ufd.Udid));
        Assert.Equal(zip, ufd.Extraction);
        Assert.Equal(sha, ufd.Sha256Of(name));
        Assert.DoesNotContain(ufd.Summary(), x => x.Value.Contains("12345") || x.Name.Contains("Password"));
    }

    [Fact]
    public void A_utf8_bom_ufd_round_trips_and_a_missing_FileDump_falls_back_to_the_zip()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_c.Root, "ufd-bom")).FullName;
        var zip = Path.Combine(dir, "only.zip");
        File.WriteAllBytes(zip, [1, 2, 3]);
        var body = "[Dumps]\nFileDump=missing.zip\n[General]\nFullName=Café\nExtractionType=AdvancedLogical\n[SHA256]\nmissing.zip=AA\n";
        var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        File.WriteAllBytes(Path.Combine(dir, "a.ufd"), [.. bom, .. Encoding.UTF8.GetBytes(body)]);

        var ufd = UfdInfo.ForExtraction(zip)!;
        Assert.Equal("Café", ufd.Get("General", "FullName"));
        Assert.Equal(zip, ufd.Extraction); // FileDump does not exist: the only zip beside the UFD
    }

    [Fact]
    public async Task Detect_and_Stage_reject_traversal_and_leave_other_zips_alone()
    {
        var plain = Path.Combine(_c.Root, "plain.zip");
        using (var z = ZipFile.Open(plain, ZipArchiveMode.Create))
            Add(z, "readme.txt", "hello");
        Assert.Null(UfedLogical.Detect(plain));
        Assert.Null(UfedLogical.Choose(plain));

        var ffs = Path.Combine(_c.Root, "ffs.zip");
        using (var z = ZipFile.Open(ffs, ZipArchiveMode.Create))
            Add(z, "filesystem1/private/var/mobile/a.db", "sms");
        Assert.Null(UfedLogical.Detect(ffs));
        using (var x = ExtractionFiles.Open(ffs)!)
        {
            Assert.Equal("zip", x.Kind);
            Assert.True(x.Contains("filesystem1/private/var/mobile/a.db"));
            Assert.False(x.Contains("private/var/mobile/a.db")); // not an iTunes backup: no domain alias
        }

        var ufed = ZipWithSnapshot(Path.Combine(_c.Root, "stage.zip"), ("Manifest.plist", "plist"), ("Manifest.db", "db"), ("3d/file", "bytes"));
        var snap = UfedLogical.Detect(ufed);
        Assert.Equal((Snap, Udid), (snap!.Prefix, snap.Udid));
        var staged = await UfedLogical.Stage(ufed, _c.Root, null, new Progress<string>(s => Assert.Contains("Staging", s)));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(staged.Folder, "3d", "file")));
        Assert.True((File.GetAttributes(Path.Combine(staged.Folder, "Manifest.db")) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("bytes"u8.ToArray())), staged.Files.Single(f => f.Path == "3d/file").Sha256);

        var evil = Path.Combine(_c.Root, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            Add(z, Snap + "Manifest.plist", "plist");
            Add(z, Snap + "Manifest.db", "db");
            Add(z, Snap + "../" + "../evil.txt", "nope");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => UfedLogical.Stage(evil, _c.Root, null));
        Assert.False(File.Exists(Path.Combine(_c.Root, "evil.txt")));

        var bad = Path.Combine(_c.Root, "bad-hash.zip");
        File.Copy(ufed, bad);
        var ufdPath = Path.Combine(_c.Root, "bad-hash.ufd");
        File.WriteAllText(ufdPath, "[SHA256]\nbad-hash.zip=" + new string('a', 64) + "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => UfedLogical.Stage(bad, _c.Root, UfdInfo.Parse(ufdPath)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UfedLogical.Stage(ufed, _c.Root, null, null, cts.Token));
    }

    [Fact]
    public void A_plain_ufade_zip_indexes_the_backup_the_afc_media_and_the_other_services()
    {
        var id = new string('a', 40);
        var zip = Path.Combine(_c.Root, "plain-ufade.zip");
        var db = Path.Combine(_c.Root, "plain.db");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE Files(fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);" +
                              "INSERT INTO Files VALUES($id,'HomeDomain','Library/SMS/sms.db',1,NULL);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(db, Snap + "Manifest.db");
            Add(z, Snap + "Manifest.plist", "plist");
            Add(z, Snap + id[..2] + "/" + id, "sms");
            Add(z, "iPhoneDump/AFC Service/DCIM/a.jpg", "jpeg");
            Add(z, "iPhoneDump/Applications/com.example.app/Documents/note.txt", "note");
            Add(z, "iPhoneDump/Lockdown Service/PhoneInfo.xml", "<p/>");
        }

        using var x = ExtractionFiles.Open(zip)!;
        Assert.Equal("UFED advanced logical", x.Kind);
        Assert.False(x.Locked);
        Assert.Equal("sms", x.Read("HomeDomain/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("sms", x.Read("private/var/mobile/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("jpeg", x.Read("private/var/mobile/Media/DCIM/a.jpg", s => new StreamReader(s).ReadToEnd()));
        Assert.Contains("iPhoneDump/Applications/com.example.app/Documents/note.txt", x.Files().Select(f => f.Path));
        Assert.Contains("iPhoneDump/Lockdown Service/PhoneInfo.xml", x.Files().Select(f => f.Path));
        // hashed names Manifest.db maps stay behind the index; the backup's own files are listed where the zip has them
        Assert.DoesNotContain(x.Files(), f => f.Path.EndsWith("/" + id, StringComparison.Ordinal));
        Assert.Contains(Snap + "Manifest.plist", x.Files().Select(f => f.Path));
    }

    [Fact]
    public void An_encrypted_ufade_zip_opens_with_the_ufd_password_and_the_decision_helper_picks_itunes()
    {
        var backup = BackupTests.BuildEncryptedBackup(Path.Combine(_c.Root, "enc"), "12345");
        var zip = Path.Combine(_c.Root, "Apple_test.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
                z.CreateEntryFromFile(file, Snap + Path.GetRelativePath(backup, file).Replace('\\', '/'));
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        File.WriteAllText(Path.Combine(_c.Root, "Apple_test.ufd"),
            "[FileDump]\nType=ZIPfolder\nZIPLogicalPath=iPhoneDump\n[General]\nExtractionType=AdvancedLogical\nVendor=Apple\nBackupPassword=12345\nGUID=" +
            Udid + "\nFullName=Phone\n[Dumps]\nFileDump=Apple_test.zip\n[SHA256]\nApple_test.zip=" + sha + "\n");

        var choice = UfedLogical.Choose(zip);
        Assert.Equal(("ileapp", "itunes", "12345"), (choice!.Tool, choice.InputType, choice.Password));
        var args = ExtractionRunner.Arguments(new ExtractionOptions("itunes", zip, _c.Root, null, null, choice.Password), "ileapp", null, null);
        Assert.Contains("--image_password_env", args);
        Assert.Contains("ALEAPP_READER_IMAGE_PW", args);
        Assert.DoesNotContain("12345", string.Join("\n", args));

        using var x = ExtractionFiles.Open(zip)!;
        Assert.False(x.Locked);
        Assert.Equal("sms", x.Read(BackupTests.SamplePath, s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("sms", x.Read("private/var/mobile/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        Assert.Throws<CryptographicException>(() => ExtractionFiles.Open(zip, "wrong"));
    }

    [Fact]
    public void Device_paths_map_afc_and_an_itunes_source_recorded_as_private_var()
    {
        Assert.Equal("/private/var/mobile/Media/DCIM/a.jpg", EvidenceOrigin.DevicePath("iPhoneDump/AFC Service/DCIM/a.jpg"));
        Assert.Equal("/private/var/mobile/Media/DCIM/a.jpg", EvidenceOrigin.DevicePath("AFC Service/DCIM/a.jpg"));
        Assert.Equal("/Applications/com.x/a", EvidenceOrigin.DevicePath("iPhoneDump/Applications/com.x/a"));
        Assert.Equal("iPhoneDump/Lockdown Service/PhoneInfo.xml", EvidenceOrigin.DevicePath("iPhoneDump/Lockdown Service/PhoneInfo.xml"));
        Assert.Equal("/private/var/mobile/a.db", EvidenceOrigin.DevicePath("filesystem1/private/var/mobile/a.db"));
        Assert.Contains("HomeDomain/Library/SMS/sms.db", EvidenceOrigin.BackupKeys("private/var/mobile/Library/SMS/sms.db"));
        Assert.Contains("AppDomain-com.foo/Library/a", EvidenceOrigin.BackupKeys("private/var/mobile/Containers/Data/Application/com.foo/Library/a"));

        var dir = Path.Combine(_c.Root, "itunes");
        var id = new string('b', 40);
        Directory.CreateDirectory(Path.Combine(dir, id[..2]));
        File.WriteAllText(Path.Combine(dir, id[..2], id), "sms");
        var db = Path.Combine(dir, "Manifest.db");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE Files(fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER);" +
                              "INSERT INTO Files VALUES($id,'HomeDomain','Library/SMS/sms.db',1);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using var x = ExtractionFiles.Open(dir)!;
        Assert.Equal("iTunes backup", x.Kind);
        Assert.True(x.Contains("private/var/mobile/Library/SMS/sms.db"));
        Assert.Equal("sms", x.Read("private/var/mobile/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
    }

    [Fact]
    public void The_staged_backup_is_not_the_evidence_the_manifest_records()
    {
        var manifest = Path.Combine(_c.Root, "_lava_data.lava");
        File.WriteAllText(manifest, """{"param_input":"C:/out/_lexis_itunes/staged","param_type":"itunes","artifacts":{}}""");
        var zip = Path.Combine(_c.Root, "Apple_test.zip");
        File.WriteAllBytes(zip, [9]);
        UfedLogical.RememberEvidence(manifest, zip);
        var node = JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal(Path.GetFullPath(zip), (string)node["param_input"]!);
        Assert.Equal("itunes", (string)node["param_type"]!);
    }

    /// <summary>Every member of a synthetic UFADE-shaped zip, as UFADE lays it out (perf_logical_plus, l_type "UFED"):
    /// iPhoneDump/Backup Service/&lt;UDID&gt;/Snapshot/ (the iTunes backup), AFC Service, Applications, Lockdown Service.</summary>
    (string Zip, int Members) UfadeShapedZip(string name, Action<ZipArchive>? snapshot = null)
    {
        var id = new string('c', 40);
        var orphan = new string('d', 40);
        var db = Path.Combine(_c.Root, name + ".db");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE Files(fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);" +
                              "INSERT INTO Files VALUES($id,'HomeDomain','Library/SMS/sms.db',1,NULL);" +
                              "INSERT INTO Files VALUES($e,'MediaDomain',NULL,1,NULL);" + // a NULL path once made the whole zip unreadable
                              "INSERT INTO Files VALUES('ee'||$e,'HomeDomain','Library',2,NULL);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$e", new string('e', 40));
            cmd.ExecuteNonQuery();
        }
        var zip = Path.Combine(_c.Root, name + ".zip");
        var members = 0;
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Put(string n, string t) { Add(z, n, t); members++; }
            z.CreateEntryFromFile(db, Snap + "Manifest.db"); members++;
            Put(Snap + "Manifest.plist", "plist");
            Put(Snap + "Info.plist", "info");
            Put(Snap + "Status.plist", "status");
            Put(Snap + id[..2] + "/" + id, "sms");
            Put(Snap + new string('e', 2) + "/" + new string('e', 40), "media-root");
            Put(Snap + orphan[..2] + "/" + orphan, "orphan"); // not listed in Manifest.db
            z.CreateEntry("iPhoneDump/AFC Service/Downloads/"); // a folder entry: not a file
            Put("iPhoneDump/AFC Service/Downloads/Report.PDF", "upper");
            Put("iPhoneDump/AFC Service/Downloads/report.pdf", "lower"); // differs only in case (iOS APFS is case-sensitive)
            Put("iPhoneDump/AFC Service/Recordings/Registrazione è.m4a", "nfc");
            Put("iPhoneDump/AFC Service/Recordings/Registrazione è.m4a", "nfd");
            Put("iPhoneDump/AFC Service/Notes/a:b?.txt", "windows-invalid");
            Put("iPhoneDump/AFC Service/Books/dup.epub", "first");
            Put("iPhoneDump/AFC Service/Books/dup.epub", "second"); // the same name stored twice
            Put("iPhoneDump/Applications/com.example.app/note.txt", "note");
            Put("iPhoneDump/Lockdown Service/PhoneInfo.xml", "<p/>");
            Put("iPhoneDump/Lockdown Service/device_values.plist", "values");
            snapshot?.Invoke(z);
        }
        return (zip, members);
    }

    static List<string> Contents(ExtractionFiles x) =>
        x.Files().Select(f => x.Read(f.Path, s => new StreamReader(s).ReadToEnd()) ?? "<unreadable:" + f.Path + ">").Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Every_member_of_a_ufade_zip_is_indexed_once_and_reads_back()
    {
        var (zip, members) = UfadeShapedZip("count");
        using var x = ExtractionFiles.Open(zip)!;
        Assert.Equal("UFED advanced logical", x.Kind);
        Assert.Equal(members, x.Files().Count); // count in = count indexed
        var contents = Contents(x);
        Assert.Equal(["<p/>", "first", "info", "lower", "media-root", "nfc", "nfd", "note", "orphan", "plist", "second", "sms", "status", "upper", "values", "windows-invalid"],
                     contents.Where(c => c != contents.FirstOrDefault(v => v.StartsWith("SQLite"))).ToList());
        Assert.Equal("upper", x.Read("private/var/mobile/Media/Downloads/Report.PDF", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("lower", x.Read("private/var/mobile/Media/Downloads/report.pdf", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("first", x.Read("private/var/mobile/Media/Books/dup.epub", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("second", x.Read("private/var/mobile/Media/Books/dup~2.epub", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("sms", x.Read("private/var/mobile/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("media-root", x.Read("MediaDomain", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("info", x.Read(Snap + "Info.plist", s => new StreamReader(s).ReadToEnd()));
    }

    [Fact]
    public void A_locked_encrypted_ufade_zip_still_lists_every_member()
    {
        var backup = BackupTests.BuildEncryptedBackup(Path.Combine(_c.Root, "locked"), "12345");
        var zip = Path.Combine(_c.Root, "locked.zip");
        var members = 0;
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
            { z.CreateEntryFromFile(file, Snap + Path.GetRelativePath(backup, file).Replace('\\', '/')); members++; }
            Add(z, "iPhoneDump/AFC Service/Downloads/a.txt", "a"); members++;
        }
        using (var locked = ExtractionFiles.Open(zip)!) // no .ufd, no password
        {
            Assert.True(locked.Locked);
            Assert.Equal(members, locked.Files().Count); // before: only the AFC file, the whole backup was missing
        }
        using var open = ExtractionFiles.Open(zip, "12345")!;
        Assert.False(open.Locked);
        Assert.Equal(members, open.Files().Count);
        Assert.Equal("sms", open.Read(BackupTests.SamplePath, s => new StreamReader(s).ReadToEnd()));
    }

    [Fact]
    public async Task Staging_carries_every_member_and_the_files_outside_the_backup_for_ileapp()
    {
        var (zip, members) = UfadeShapedZip("stage-all");
        var staged = await UfedLogical.Stage(zip, _c.Root, null);
        Assert.Equal(members, staged.Files.Count);
        string Text(string rel) => File.ReadAllText(Path.Combine(staged.Folder, rel.Replace('/', Path.DirectorySeparatorChar)));
        const string x = UfedLogical.ExtraDir + "/";
        Assert.Equal("values", Text(x + "iPhoneDump/Lockdown Service/device_values.plist"));
        Assert.Equal("note", Text(x + "iPhoneDump/Applications/com.example.app/note.txt"));
        Assert.Equal("first", Text(x + "private/var/mobile/Media/Books/dup.epub"));
        Assert.Equal("second", Text(x + "private/var/mobile/Media/Books/dup~2.epub")); // was: the read-only first copy aborted the staging
        Assert.Equal("windows-invalid", Text(x + "private/var/mobile/Media/Notes/a_b_.txt"));
        Assert.Contains(staged.Files, f => f.Path == "Info.plist");
        Assert.True(File.Exists(Path.Combine(staged.Folder, "Manifest.db")));
        var sms = staged.Files.Single(f => f.Path.EndsWith(new string('c', 40), StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("sms"u8.ToArray())), sms.Sha256);
    }

    [Fact]
    public void Index_keys_and_unique_names()
    {
        Assert.Equal("private/var/mobile/Media/DCIM/a.jpg", UfedLogical.IndexKey("iPhoneDump/AFC Service/DCIM/a.jpg"));
        Assert.Equal("private/var/mobile/Media/a", UfedLogical.IndexKey("AFC Service/a"));
        Assert.Equal("iPhoneDump/XAFC Service/a", UfedLogical.IndexKey("iPhoneDump/XAFC Service/a"));
        Assert.Equal("iPhoneDump/Applications/x/AFC Service/a", UfedLogical.IndexKey("iPhoneDump/Applications/x/AFC Service/a"));
        Assert.Equal("a/b~3.txt", UfedLogical.Unique("a/b.txt", k => k is "a/b.txt" or "a/b~2.txt"));
        Assert.Equal("a.d/b~2", UfedLogical.Unique("a.d/b", k => k == "a.d/b"));
        Assert.Equal("x/_CON.txt/a_b_/c_", UfedLogical.DiskSafe("x/CON.txt/a<b>/c."));
    }

    [Fact]
    public void The_ufd_summary_shows_the_udid_and_device_class_ufade_writes()
    {
        var path = Path.Combine(_c.Root, "sum.ufd");
        File.WriteAllText(path, "[General]\nGUID=" + Udid + "\nDevice=IPHONE\nIsEncrypted=True\nBackupPassword=12345\n");
        var sum = UfdInfo.Parse(path).Summary();
        Assert.Contains(sum, s => s.Name == "UDID" && s.Value == Udid);
        Assert.Contains(sum, s => s.Name == "Device class" && s.Value == "IPHONE");
        Assert.DoesNotContain(sum, s => s.Value == "12345");
    }

    static void Add(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name.Replace('\\', '/'));
        using var s = e.Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }

    string ZipWithSnapshot(string path, params (string Name, string Text)[] files)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files) Add(z, Snap + name, text);
        return path;
    }

    static byte[] Cp1252(string text)
    {
        var chars = text.ToCharArray();
        var bytes = new byte[chars.Length];
        for (var i = 0; i < chars.Length; i++)
            bytes[i] = chars[i] switch { 'é' => 0xE9, 'É' => 0xC9, _ => (byte)chars[i] };
        return bytes;
    }
}
