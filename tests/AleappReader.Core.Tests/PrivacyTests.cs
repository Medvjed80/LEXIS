using AleappReader.Core;

namespace AleappReader.Core.Tests;

/// <summary>Privacy hardening: logs carry no case content, and copies of evidence in %TEMP% are removed.</summary>
public class PrivacyTests
{
    [Fact]
    public void An_error_log_entry_has_the_exception_types_and_stack_but_not_the_messages()
    {
        Exception e;
        try { throw new InvalidOperationException("outer SECRET-NAME", new IOException("inner +390000000000 chat text")); }
        catch (Exception x) { e = x; }
        var text = ErrorText.Describe(e);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("System.IO.IOException", text);
        Assert.DoesNotContain("SECRET-NAME", text);
        Assert.DoesNotContain("+390000000000", text);
        Assert.DoesNotContain("chat text", text);
    }

    [Fact]
    public void Case_caches_in_temp_are_swept_by_age_or_all_at_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "lexis-privacy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var work = Path.Combine(root, "AleappReader");
            var old = Directory.CreateDirectory(Path.Combine(work, "extraction", "old"));
            var fresh = Directory.CreateDirectory(Path.Combine(work, "sources"));
            File.WriteAllText(Path.Combine(old.FullName, "a.bin"), "x");
            File.SetAttributes(Path.Combine(old.FullName, "a.bin"), FileAttributes.ReadOnly); // read-only copies are removed too
            File.WriteAllText(Path.Combine(fresh.FullName, "b.bin"), "y");
            Directory.SetLastWriteTimeUtc(Path.Combine(work, "extraction"), DateTime.UtcNow.AddDays(-2));

            Assert.Equal(0, TempWork.SweepCaseCaches(all: false, TimeSpan.FromHours(6), root));
            Assert.False(Directory.Exists(Path.Combine(work, "extraction")));
            Assert.True(Directory.Exists(fresh.FullName));

            Assert.Equal(0, TempWork.SweepCaseCaches(all: true, TimeSpan.Zero, root));
            Assert.False(Directory.Exists(fresh.FullName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
