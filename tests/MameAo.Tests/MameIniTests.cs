using System;
using System.Collections.Generic;
using System.IO;

using Spludlow.MameAO;

namespace MameAo.Tests;

public class MameIniTests : IDisposable
{
    private readonly string _Root;

    public MameIniTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "mame-ao-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_Root, true); } catch (Exception) { }
    }

    private MameCoreLayout Layout() => new MameCoreLayout(Path.Combine(_Root, "0288"));

    [Fact]
    public void Build_WritesEveryPathAbsolute()
    {
        MameCoreLayout layout = Layout();
        string hash = Path.Combine(_Root, "hash");

        string text = MameIni.Build(layout, hash);

        foreach (string key in new[]
                 {
                     "homepath", "inipath", "rompath", "samplepath", "artpath", "ctrlrpath", "fontpath",
                     "cheatpath", "crosshairpath", "pluginspath", "languagepath", "swpath", "hashpath",
                     "cfg_directory", "nvram_directory", "input_directory", "state_directory",
                     "snapshot_directory", "diff_directory", "comment_directory", "share_directory",
                 })
        {
            Assert.Contains(key, text);
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.Length == 0)
                continue;

            int space = line.IndexOf(' ');
            if (space <= 0)
                continue;

            string key = line[..space];
            string value = line[space..].Trim();

            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (key is "readconfig" or "writeconfig")
                continue;

            Assert.True(Path.IsPathRooted(value), $"{key} is not absolute: {value}");
        }

        Assert.Contains($"rompath                   {layout.RomDirectory}", text);
        Assert.Contains($"snapshot_directory        {layout.SnapshotDirectory}", text);
        Assert.Contains($"hashpath                  {hash}", text);
    }

    [Fact]
    public void Build_OmitsHashPathWhenUnknown()
    {
        string text = MameIni.Build(Layout(), null);

        Assert.DoesNotContain("hashpath", text);
    }

    [Fact]
    public void Write_IsAtomicAndOverwrites()
    {
        MameCoreLayout layout = Layout();
        Directory.CreateDirectory(layout.CoreDirectory);

        MameIni.Write(layout, Path.Combine(_Root, "hash-one"));
        MameIni.Write(layout, Path.Combine(_Root, "hash-two"));

        Assert.True(File.Exists(layout.IniFilename));
        Assert.False(File.Exists(layout.IniFilename + ".tmp"));
        Assert.Contains("hash-two", File.ReadAllText(layout.IniFilename));
    }
}
