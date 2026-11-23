using System.IO.Compression;

namespace AleappReader.Core.Tests;

/// <summary>1.2.12: empty or truncated audio is skipped during transcription instead of failing the batch.
/// Synthetic data only; no case content.</summary>
public sealed class Release1212Tests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("lexis-1212-");
    public void Dispose() { try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    static MediaRef Audio(string path) =>
        new("1", Path.GetFileName(path), "audio/opus", path, "Media/" + Path.GetFileName(path), path, "m", "a");

    [Fact]
    public void ReadBytes_returns_the_first_complete_copy()
    {
        var n = 0;
        var got = AudioSource.ReadBytes(() => { n++; return [1, 2, 3]; }, 3);
        Assert.Equal([1, 2, 3], got);
        Assert.Equal(1, n);
    }

    [Fact]
    public void ReadBytes_retries_once_when_the_first_read_throws()
    {
        var n = 0;
        var got = AudioSource.ReadBytes(() =>
        {
            n++;
            if (n == 1) throw new IOException("zip closed");
            return [9, 8, 7, 6];
        }, 4);
        Assert.Equal([9, 8, 7, 6], got);
        Assert.Equal(2, n);
    }

    [Fact]
    public void ReadBytes_retries_once_when_the_first_copy_is_short()
    {
        var n = 0;
        var got = AudioSource.ReadBytes(() =>
        {
            n++;
            return n == 1 ? [1, 2] : [1, 2, 3, 4];
        }, 4);
        Assert.Equal([1, 2, 3, 4], got);
        Assert.Equal(2, n);
    }

    [Fact]
    public void ReadBytes_retries_when_the_first_copy_is_empty_but_a_size_is_known()
    {
        var n = 0;
        var got = AudioSource.ReadBytes(() => { n++; return n == 1 ? [] : [5, 6]; }, 2);
        Assert.Equal([5, 6], got);
        Assert.Equal(2, n);
    }

    [Fact]
    public void ReadBytes_skips_a_genuinely_empty_file_without_a_second_read()
    {
        var n = 0;
        Assert.Null(AudioSource.ReadBytes(() => { n++; return []; }, 0));
        Assert.Equal(1, n);
    }

    [Fact]
    public void ReadBytes_skips_after_two_failures()
    {
        var n = 0;
        Assert.Null(AudioSource.ReadBytes(() => { n++; throw new ObjectDisposedException("zip"); }, 10));
        Assert.Equal(2, n);
        n = 0;
        Assert.Null(AudioSource.ReadBytes(() => { n++; return [1]; }, 10)); // both copies short
        Assert.Equal(2, n);
    }

    [Fact]
    public void An_empty_opus_on_disk_is_skipped_and_a_non_empty_one_is_loaded()
    {
        var empty = Path.Combine(_dir.FullName, "placeholder.opus");
        File.WriteAllBytes(empty, []);
        Assert.False(AudioSource.TryForTranscription(Audio(empty), [_dir.FullName], out var audio, out var name, out var silk, out var skip));
        Assert.Null(audio);
        Assert.Equal(AudioSource.SkippedEmpty, skip);
        Assert.Equal("placeholder.opus", name);
        Assert.Null(silk);

        var voice = Path.Combine(_dir.FullName, "voice.opus");
        File.WriteAllBytes(voice, [0x4F, 0x67, 0x67, 0x53, 1, 2, 3, 4]); // "OggS" head, not decoded here
        Assert.True(AudioSource.TryForTranscription(Audio(voice), [_dir.FullName], out audio, out name, out silk, out skip));
        Assert.Equal(File.ReadAllBytes(voice), audio);
        Assert.Equal("voice.opus", name);
        Assert.Null(silk);
        Assert.Null(skip);
    }

    [Fact]
    public void A_zero_byte_zip_entry_is_skipped()
    {
        var zip = Path.Combine(_dir.FullName, "media.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntry("chat/PTT-000.opus"); // 0-byte entry: a typical undownloaded voice note
            using var s = z.CreateEntry("chat/PTT-001.opus").Open();
            s.Write([0x4F, 0x67, 0x67, 0x53, 9]);
        }

        using var archive = ZipFile.OpenRead(zip);
        var empty = archive.GetEntry("chat/PTT-000.opus")!;
        Assert.Equal(0, empty.Length);
        var bytes = AudioSource.ReadBytes(() =>
        {
            using var st = empty.Open();
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return ms.ToArray();
        }, empty.Length);
        Assert.Null(bytes);

        var voice = archive.GetEntry("chat/PTT-001.opus")!;
        var got = AudioSource.ReadBytes(() =>
        {
            using var st = voice.Open();
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return ms.ToArray();
        }, voice.Length);
        Assert.Equal(5, got!.Length);
    }

    [Fact]
    public async Task WriteFully_closes_the_file_and_matches_the_expected_length()
    {
        var path = Path.Combine(_dir.FullName, "audio.opus");
        var data = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        await AudioSource.WriteFully(path, data, CancellationToken.None);
        Assert.False(File.Exists(path + ".part"));
        var info = new FileInfo(path);
        Assert.True(info.Exists);
        Assert.Equal(data.Length, info.Length);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Equal(data.Length, fs.Length); // closed by the writer: another handle can read it
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WriteFully_and_Transcribe_reject_empty_audio_before_ffmpeg()
    {
        var path = Path.Combine(_dir.FullName, "audio.opus");
        await Assert.ThrowsAsync<EmptyAudioException>(() => AudioSource.WriteFully(path, [], CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));

        var err = await Assert.ThrowsAsync<EmptyAudioException>(() =>
            WhisperCpp.Transcribe("whisper-cli-not-used", "ggml-tiny.bin", "clip.opus", [], CancellationToken.None));
        Assert.Equal(AudioSource.SkippedEmpty, err.Message);
    }

    [Fact]
    public void Unusable_input_is_an_empty_file_or_ffmpeg_EOF_not_another_ffmpeg_error()
    {
        Assert.True(AudioSource.IsUnusableInput(new EmptyAudioException()));
        Assert.True(AudioSource.IsUnusableInput(new AiException(
            "ffmpeg ended with code -541478725: Error opening input: End of file")));
        Assert.True(AudioSource.IsUnusableInput(new AiException(
            "ffmpeg ended with code 1: Error opening input file C:\\Users\\x\\AppData\\Local\\Temp\\lexis-whisper-a.ajx\\audio.opus. Error opening input files: End of file")));
        Assert.False(AudioSource.IsUnusableInput(new AiException("ffmpeg ended with code 1: Invalid argument")));
        Assert.False(AudioSource.IsUnusableInput(new AiException("whisper-cli ended with code 1: failed to load model")));
        Assert.False(AudioSource.IsUnusableInput(new IOException("disk full")));
    }

    [Fact]
    public void A_skip_is_logged_without_a_path_or_case_content()
    {
        var log = Path.Combine(_dir.FullName, "errors.log");
        var secret = Path.Combine(_dir.FullName, "case", "PTT-000.opus");
        ErrorText.Append(AudioSource.SkippedEmpty, new AiException(
            $"ffmpeg ended with code -541478725: Error opening input file {secret}"), log);
        var text = File.ReadAllText(log);
        Assert.Contains(AudioSource.SkippedEmpty, text);
        Assert.Contains("AleappReader.Core.AiException", text);
        Assert.Contains("message withheld", text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("PTT-000.opus", text);
        Assert.DoesNotContain("-541478725", text);
        Assert.DoesNotContain(_dir.FullName, text);
    }

    [Fact]
    public void A_batch_of_files_skips_empty_ones_and_keeps_the_rest()
    {
        var empty = Path.Combine(_dir.FullName, "a.opus");
        var voice = Path.Combine(_dir.FullName, "b.opus");
        var missing = Path.Combine(_dir.FullName, "gone.opus");
        File.WriteAllBytes(empty, []);
        File.WriteAllBytes(voice, [0x4F, 0x67, 0x67, 0x53, 1]);
        var files = new[] { Audio(empty), Audio(voice), Audio(missing) };
        var loaded = new List<string>();
        var skipped = 0;
        foreach (var m in files)
        {
            if (AudioSource.TryForTranscription(m, [_dir.FullName], out _, out var name, out _, out var skip))
                loaded.Add(name);
            else
            {
                skipped++;
                Assert.Equal(AudioSource.SkippedEmpty, skip);
            }
        }
        Assert.Equal(["b.opus"], loaded);
        Assert.Equal(2, skipped);
    }
}
