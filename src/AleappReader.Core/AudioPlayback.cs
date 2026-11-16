using System.Security.Cryptography;

namespace AleappReader.Core;

/// <summary>
/// A file the in-app player can open. The Windows player plays WAV, MP3, M4A, AAC and WMA itself.
/// SILK (WeChat, QQ) is decoded by <see cref="SilkAudio"/>. Anything else is converted with ffmpeg to the
/// 16 kHz mono WAV whisper.cpp also uses, cached beside the case's decoded audio. The original is only read.
/// </summary>
public static class AudioPlayback
{
    /// <summary>Extensions the in-app player plays without conversion.</summary>
    public static readonly string[] NativeExtensions = [".wav", ".mp3", ".m4a", ".aac", ".wma"];

    public static bool PlaysNatively(string path) =>
        NativeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The file to give the player, and a note when it is a converted copy (null when the original plays).</summary>
    public static async Task<(string File, string? Note)> Prepare(MediaRef m, CancellationToken ct = default)
    {
        var (file, silk, folder) = await Task.Run(() =>
        {
            if (m.AudioFolders is { Length: > 0 } folders)
            {
                var (decoded, sha) = SilkAudio.Playable(m, folders);
                return (decoded, sha, folders[0]);
            }
            return (m.LocalFile(), (string?)null, (string?)null);
        }, ct);
        if (silk is not null)
            return (file, $"SILK audio, converted to WAV for listening; original unchanged.\nSHA-256 {silk}\nWAV: {file}\nDecoder: {SilkAudio.Decoder}");
        if (PlaysNatively(file)) return (file, null);
        var ffmpeg = WhisperCpp.BundledFfmpeg
            ?? throw new IOException("This audio cannot be played directly and LEXIS's ffmpeg could not be written to this computer.");
        var wav = await CachedWav(file, folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "audio"), ffmpeg, ct);
        return (wav, "Converted to WAV for listening; original unchanged.");
    }

    /// <summary><c>&lt;SHA-256 of the source&gt;.preview.wav</c> in <paramref name="folder"/>, converted once.</summary>
    static async Task<string> CachedWav(string source, string folder, string ffmpeg, CancellationToken ct)
    {
        string sha;
        await using (var input = File.OpenRead(source))
            sha = Convert.ToHexStringLower(SHA256.HashData(input));
        Directory.CreateDirectory(folder);
        var wav = Path.Combine(folder, sha + ".preview.wav");
        if (File.Exists(wav)) return wav;
        var temp = Path.Combine(folder, sha + "." + Environment.ProcessId + ".tmp.wav");
        try
        {
            await WhisperCpp.ToWav(ffmpeg, source, temp, ct);
            try { File.Move(temp, wav); }
            catch (IOException) when (File.Exists(wav)) { File.Delete(temp); }
        }
        catch
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
        return wav;
    }
}
