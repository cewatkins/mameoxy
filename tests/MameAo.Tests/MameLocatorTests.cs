using System;
using System.Collections.Generic;
using System.IO;

using Spludlow.MameAO;

namespace MameAo.Tests;

public class MameLocatorTests
{
    [Theory]
    [InlineData("0.288 (mame0288-847-g84e1e905e45-dirty)", "0288")]
    [InlineData("0.288", "0288")]
    [InlineData("0.285", "0285")]
    [InlineData("0.9", "0009")]
    [InlineData("mame0288", "0288")]
    public void VersionToDirectoryName(string text, string expected)
    {
        Assert.Equal(expected, MameLocator.VersionToDirectoryName(text));
    }

    [Fact]
    public void VersionToDirectoryName_RejectsGarbage()
    {
        Assert.Throws<MameNotFoundException>(() => MameLocator.VersionToDirectoryName("not a version"));
    }

    [Fact]
    public void ParseConfigValue_ReadsPaddedShowConfig()
    {
        string text = "homepath                  /home/me/mame\nrompath                   roms;roms/bios\n";

        Assert.Equal("/home/me/mame", MameLocator.ParseConfigValue(text, "homepath"));
        Assert.Equal("roms;roms/bios", MameLocator.ParseConfigValue(text, "ROMPATH"));
        Assert.Null(MameLocator.ParseConfigValue(text, "hashpath"));
    }

    [Fact]
    public void Which_FindsExecutableOnPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mame-ao-which-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string executable = Path.Combine(directory, "mame");
            File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Func<string, string> env = name => name == "PATH" ? directory : null;

            Assert.Equal(executable, MameLocator.Which("mame", env));
            Assert.Null(MameLocator.Which("definitely-not-here", env));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Which_RejectsNonExecutableFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mame-ao-which-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "mame"), "");

            Func<string, string> env = name => name == "PATH" ? directory : null;

            Assert.Null(MameLocator.Which("mame", env));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Locate_ExplainsWhatItTried()
    {
        Func<string, string> env = name => name == "PATH" ? "/nonexistent" : null;

        MameNotFoundException error = Assert.Throws<MameNotFoundException>(
            () => MameLocator.Locate("/nonexistent/mame", env));

        Assert.Contains("/nonexistent/mame", error.Message);
        Assert.NotEmpty(error.Attempts);
    }

    [Fact]
    public void ResolveHashPath_FindsDirectoryContainingXml()
    {
        string root = Path.Combine(Path.GetTempPath(), "mame-ao-hash-" + Guid.NewGuid().ToString("N"));
        string hash = Path.Combine(root, "hash");
        Directory.CreateDirectory(hash);

        try
        {
            File.WriteAllText(Path.Combine(hash, "mame.xml"), "<mame/>");

            Assert.Equal(Path.GetFullPath(hash), MameLocator.ResolveHashPath(hash, null, null));
            Assert.Equal(Path.GetFullPath(hash), MameLocator.ResolveHashPath("hash", root, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
