using System.Text;

namespace AleappReader.Core;

/// <summary>Exceptions as text for a log: the types and the stack, never the messages, which can carry file paths, names or
/// record contents of a case (a SQLite error quoting a value, an IOException naming an evidence file).</summary>
public static class ErrorText
{
    public static string Describe(Exception e)
    {
        var sb = new StringBuilder();
        for (var depth = 0; e is not null && depth < 8; depth++, e = e.InnerException!)
        {
            if (depth > 0) sb.AppendLine("--- inner");
            sb.AppendLine(e.GetType().FullName + " (message withheld: it may contain case data)");
            if (e.StackTrace is { } stack) sb.AppendLine(stack);
            if (e.InnerException is null) break;
        }
        return sb.ToString().TrimEnd();
    }

    public const long MaxLogBytes = 256 * 1024;

    /// <summary>The rolling log in the AleappReader folder under LocalApplicationData, the same file the unhandled-exception handler uses.</summary>
    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "errors.log");

    /// <summary>Appends the exception's types and stack, never its messages (they can carry case data).</summary>
    public static void Append(Exception e, string? path = null) => Write(Describe(e), path);

    /// <summary>Appends a fixed note, and optionally an exception's types and stack. <paramref name="note"/> must not
    /// name a file, a person or a record of a case.</summary>
    public static void Append(string note, Exception? e = null, string? path = null) =>
        Write(note + (e is null ? "" : Environment.NewLine + Describe(e)), path);

    static void Write(string body, string? path)
    {
        path ??= DefaultLogPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{body}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
