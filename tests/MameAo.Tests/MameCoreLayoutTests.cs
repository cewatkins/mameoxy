using System;
using System.Collections.Generic;
using System.IO;

using Spludlow.MameAO;

namespace MameAo.Tests;

public class MameCoreLayoutTests : IDisposable
{
    private readonly string _Root;
    private readonly MameCoreLayout _Layout;

    public MameCoreLayoutTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "mame-ao-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
        _Layout = new MameCoreLayout(Path.Combine(_Root, "0288"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_Root, true); } catch (Exception) { }
    }

    private MameInstall Install()
    {
        string binary = Path.Combine(_Root, "fake-mame");
        File.WriteAllText(binary, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(binary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string hash = Path.Combine(_Root, "native-hash");
        Directory.CreateDirectory(hash);
        File.WriteAllText(Path.Combine(hash, "mame.xml"), "<mame/>");

        return new MameInstall
        {
            Kind = MameProviderKind.ExplicitPath,
            Prefix = new[] { binary },
            Detail = "test",
            Version = "0288",
            VersionRaw = "0.288 (test)",
            HomePath = _Root,
            HashPath = hash,
        };
    }

    [Fact]
    public void EnsureDirectories_CreatesEveryOutputDirectory()
    {
        _Layout.EnsureDirectories();

        foreach (string directory in _Layout.Directories)
            Assert.True(Directory.Exists(directory), directory);
    }

    [Fact]
    public void Prepare_WritesIniInfoAndLinks()
    {
        MameInstall install = Install();

        IReadOnlyList<string> warnings = _Layout.Prepare(install);

        Assert.Empty(warnings);
        Assert.True(File.Exists(_Layout.IniFilename));

        string ini = File.ReadAllText(_Layout.IniFilename);
        Assert.Contains(_Layout.RomDirectory, ini);
        Assert.Contains(install.HashPath, ini);

        FileSystemInfo link = new FileInfo(_Layout.BinaryLink);
        Assert.True(link.Exists);
        Assert.NotNull(link.ResolveLinkTarget(true));

        FileSystemInfo hashLink = new DirectoryInfo(_Layout.HashLink);
        Assert.True(hashLink.Exists);
        Assert.NotNull(hashLink.ResolveLinkTarget(true));

        string info = File.ReadAllText(_Layout.InfoFilename);
        Assert.Contains("MameVersion\t0288", info);
        Assert.Contains("MameProvider\tExplicitPath", info);
    }

    [Fact]
    public void Prepare_IsIdempotent()
    {
        MameInstall install = Install();

        _Layout.Prepare(install);
        IReadOnlyList<string> warnings = _Layout.Prepare(install);

        Assert.Empty(warnings);
        Assert.True(File.Exists(_Layout.IniFilename));
        Assert.True(new FileInfo(_Layout.BinaryLink).Exists);
    }

    [Fact]
    public void Prepare_ToleratesMissingHashDirectory()
    {
        MameInstall install = new MameInstall
        {
            Kind = MameProviderKind.PathLookup,
            Prefix = new[] { "/bin/true" },
            Detail = "test",
            Version = "0288",
            VersionRaw = "0.288",
            HashPath = null,
        };

        IReadOnlyList<string> warnings = _Layout.Prepare(install);

        Assert.Empty(warnings);
        Assert.DoesNotContain("hashpath", File.ReadAllText(_Layout.IniFilename));
        Assert.False(Directory.Exists(_Layout.HashLink));
    }

    [Fact]
    public void ForVersion_RejectsEmptyVersion()
    {
        Assert.Throws<ArgumentException>(() => MameCoreLayout.ForVersion(_Root, ""));
    }
}
