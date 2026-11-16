using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class ExtractionPluginListTests
{
    [Fact]
    public void Plugin_list_failure_includes_the_last_lines_of_stderr()
    {
        var stderr = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"line {i}"));
        var msg = ExtractionRunner.PluginListFailure("ALEAPP", stderr, 1);
        Assert.Contains("ALEAPP did not list its plugins", msg);
        Assert.Contains("Exit code 1.", msg);
        Assert.DoesNotContain("line 5", msg);
        Assert.Contains("line 6", msg);
        Assert.Contains("line 25", msg);
    }

    [Fact]
    public void Plugin_list_failure_without_stderr_stays_a_short_message()
    {
        var msg = ExtractionRunner.PluginListFailure("iLEAPP", "  \n  ");
        Assert.Equal("iLEAPP did not list its plugins. Check the Python environment.", msg);
    }

    [Fact]
    public async Task ListPlugins_of_a_built_exe_without_plugins_json_explains_the_missing_file()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-plugins-");
        try
        {
            var exe = Path.Combine(dir.FullName, "aleapp.exe");
            File.WriteAllBytes(exe, [0]);
            var inst = new AleappInstall(null, null, exe, "aleapp");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractionRunner.ListPluginsAsync(inst));
            Assert.Contains("built executable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ExtractionRunner.PluginsFile, ex.Message);
            Assert.Contains(Path.Combine(dir.FullName, ExtractionRunner.PluginsFile), ex.Message);
            Assert.Contains("module list cannot be shown", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ListPlugins_reads_plugins_json_beside_a_built_exe()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-plugins-");
        try
        {
            var exe = Path.Combine(dir.FullName, "ileapp.exe");
            File.WriteAllBytes(exe, [0]);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, ExtractionRunner.PluginsFile),
                """[{"name":"lexisWhatsApp","category":"Messaging","artifact":"WhatsApp","module":"LEXIS_WhatsApp"}]""");
            var inst = new AleappInstall(null, null, exe, "ileapp");
            var list = await ExtractionRunner.ListPluginsAsync(inst);
            var one = Assert.Single(list);
            Assert.Equal("LEXIS_WhatsApp", one.Module);
            Assert.Equal("WhatsApp", one.Artifact);
        }
        finally { dir.Delete(true); }
    }
}
