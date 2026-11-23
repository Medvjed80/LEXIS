using System.Diagnostics.CodeAnalysis;

namespace AleappReader.Core;

/// <summary>The audio file has no usable bytes (empty, truncated, or unreadable after a retry). The transcription
/// batch continues; this is not a hard failure of the run.</summary>
public sealed class EmptyAudioException() : AiException(AudioSource.SkippedEmpty);

/// <summary>Loading an audio file for whisper.cpp: a complete copy, retried once, or a skip when the source is
/// genuinely empty or still unreadable (a WhatsApp placeholder never downloaded, a short zip read, a closed extraction).</summary>
public static class AudioSource
{
    public const string SkippedEmpty = "Skipped: empty/unavailable audio file";

    /// <summary>ffmpeg's AVERROR_EOF, reported when the input file is empty or truncated so no header can be read.</summary>
    public const int FfmpegEof = -541478725;

    /// <summary>Bytes of an audio file: retried once when the first read is short, empty although a size is known,
    /// or throws a readable I/O error. Null when the source is genuinely empty or still unreadable.</summary>
    public static byte[]? ReadBytes(Func<byte[]> read, long expectedSize)
    {
        if (Try(read, expectedSize, out var first, out var retryable) && first is { Length: > 0 } ok
            && (expectedSize <= 0 || ok.Length == expectedSize)) return ok;
        if (!retryable && first is { Length: 0 } && expectedSize <= 0) return null; // a known empty file: no second read needed
        if (Try(read, expectedSize, out var again, out _) && again is { Length: > 0 } ok2
            && (expectedSize <= 0 || ok2.Length == expectedSize)) return ok2;
        return null;
    }

    static bool Try(Func<byte[]> read, long expectedSize, out byte[]? bytes, out bool retryable)
    {
        try
        {
            bytes = read();
            retryable = bytes.Length == 0 && expectedSize > 0 || expectedSize > 0 && bytes.Length != expectedSize;
            return true;
        }
        catch (Exception e) when (IsRetryable(e))
        {
            bytes = null;
            retryable = true;
            return false;
        }
    }

    public static bool IsRetryable(Exception e) =>
        e is EmptyAudioException or IOException or UnauthorizedAccessException or InvalidDataException
            or ObjectDisposedException or InvalidOperationException;

    /// <summary>The file's bytes for speech to text, or false with <see cref="SkippedEmpty"/> when it cannot be read.
    /// Retries the whole load once after a readable I/O error or a short read, not when the file is known to be empty.</summary>
    public static bool TryForTranscription(MediaRef m, IReadOnlyList<string> folders,
        [NotNullWhen(true)] out byte[]? audio, out string name, out string? silkSha, out string? skip)
    {
        if (Load(m, folders, out audio, out name, out silkSha, out skip)) return true;
        if (!ShouldRetry(m)) return false;
        return Load(m, folders, out audio, out name, out silkSha, out skip);
    }

    /// <summary>A second attempt is worth it when the first one failed by error or a short read, not when the file is
    /// known to be empty on disk.</summary>
    static bool ShouldRetry(MediaRef m)
    {
        try { return m.Size != 0; }
        catch (Exception e) when (IsRetryable(e)) { return true; }
    }

    static bool Load(MediaRef m, IReadOnlyList<string> folders,
        [NotNullWhen(true)] out byte[]? audio, out string name, out string? silkSha, out string? skip)
    {
        name = m.Name;
        audio = null;
        silkSha = null;
        try
        {
            (audio, name, silkSha) = SilkAudio.ForTranscription(m, folders);
            if (audio.Length == 0) { skip = SkippedEmpty; audio = null; return false; }
            skip = null;
            return true;
        }
        catch (Exception e) when (IsRetryable(e))
        {
            skip = SkippedEmpty;
            return false;
        }
    }

    /// <summary>Writes <paramref name="audio"/> to <paramref name="path"/>, flushed and closed, then checks the length.
    /// A short write is retried once. The caller may then hand the path to ffmpeg.</summary>
    public static async Task WriteFully(string path, byte[] audio, CancellationToken ct)
    {
        if (audio.Length == 0) throw new EmptyAudioException();
        await WriteOnce(path, audio, ct).ConfigureAwait(false);
        if (new FileInfo(path).Length == audio.Length) return;
        await WriteOnce(path, audio, ct).ConfigureAwait(false);
        if (new FileInfo(path).Length != audio.Length) throw new EmptyAudioException();
    }

    static async Task WriteOnce(string path, byte[] audio, CancellationToken ct)
    {
        var part = path + ".part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
            {
                await fs.WriteAsync(audio, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(true); // the directory entry is visible to ffmpeg only after this stream is closed
            }
            File.Move(part, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether a failed transcription should be skipped so the rest of the batch can continue: an empty
    /// source, or ffmpeg reporting AVERROR_EOF on a truncated temporary file.</summary>
    public static bool IsUnusableInput(Exception e)
    {
        if (e is EmptyAudioException) return true;
        if (e is not AiException ai) return false;
        var m = ai.Message;
        return m.Contains(FfmpegEof.ToString(), StringComparison.Ordinal)
            || (m.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                && m.Contains("End of file", StringComparison.OrdinalIgnoreCase));
    }
}
