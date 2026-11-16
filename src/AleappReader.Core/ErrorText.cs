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
}
