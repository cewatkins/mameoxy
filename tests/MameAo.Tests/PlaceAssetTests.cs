using System;
using System.Data;
using System.IO;

using Spludlow.MameAO;

namespace MameAo.Tests;

/// <summary>
/// Asset placement: what ends up in the MAME rom directory, and what it points at.
/// </summary>
public class PlaceAssetTests : IDisposable
{
    private readonly string _Root;
    private readonly string _Store;
    private readonly HashStore _HashStore;

    public PlaceAssetTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "mame-ao-place-" + Guid.NewGuid().ToString("N"));
        _Store = Path.Combine(_Root, "store");
        Directory.CreateDirectory(_Root);

        _HashStore = new HashStore(_Store, filename => "unused");

        Globals.WorkerTaskReport = Reports.PlaceReportTemplate();
        Globals.LinkingEnabled = true;
        Globals.Reports ??= new Reports();
    }

    public void Dispose()
    {
        Globals.LinkingEnabled = false;

        try { Directory.Delete(_Root, true); } catch (Exception) { }
    }

    private static DataRow[] Assets(params (string Name, string Sha1)[] entries)
    {
        DataTable table = new DataTable();
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("sha1", typeof(string));

        foreach ((string name, string sha1) in entries)
            table.Rows.Add(name, sha1);

        return table.Select();
    }

    private void Store(string sha1, string content)
    {
        string source = Path.Combine(_Root, "source-" + sha1 + ".bin");
        File.WriteAllText(source, content);
        Assert.True(_HashStore.Add(source, move: false, sha1: sha1));
    }

    [Fact]
    public void PlaceAssetFiles_LinksFromStoreAndCountsMissing()
    {
        const string present = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string absent = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        Store(present, "present");

        string target = Path.Combine(_Root, "roms");

        int missing = Place.PlaceAssetFiles(
            Assets(("machine.zip", present), ("missing.zip", absent)),
            _HashStore,
            target,
            null,
            new[] { "MachineRom", "mame", "a2600" });

        Assert.Equal(1, missing);

        string placed = Path.Combine(target, "machine.zip");
        Assert.True(File.Exists(placed));

        FileSystemInfo info = new FileInfo(placed).ResolveLinkTarget(true);
        Assert.NotNull(info);
        Assert.Equal(present, Path.GetFileName(info.FullName));

        Assert.False(File.Exists(Path.Combine(target, "missing.zip")));
    }

    [Fact]
    public void PlaceAssetFiles_LeavesExistingTargetAlone()
    {
        const string sha1 = "cccccccccccccccccccccccccccccccccccccccc";

        Store(sha1, "stored");

        string target = Path.Combine(_Root, "roms");
        Directory.CreateDirectory(target);

        string existing = Path.Combine(target, "machine.zip");
        File.WriteAllText(existing, "already here");

        int missing = Place.PlaceAssetFiles(
            Assets(("machine.zip", sha1)),
            _HashStore,
            target,
            null,
            new[] { "MachineRom", "mame", "a2600" });

        Assert.Equal(0, missing);
        Assert.Equal("already here", File.ReadAllText(existing));
        Assert.Null(new FileInfo(existing).ResolveLinkTarget(true));
    }

    [Fact]
    public void PlaceAssetFiles_AppendsSuffixForSoftwareAssets()
    {
        const string sha1 = "dddddddddddddddddddddddddddddddddddddddd";

        Store(sha1, "software");

        string target = Path.Combine(_Root, "roms");

        Place.PlaceAssetFiles(
            Assets(("cart.bin", sha1)),
            _HashStore,
            target,
            ".rom",
            new[] { "SoftwareRom", "a2600", "game" });

        Assert.True(File.Exists(Path.Combine(target, "cart.bin.rom")));
    }

    [Fact]
    public void PlaceFiles_CopiesWhenLinkingDisabled()
    {
        Globals.LinkingEnabled = false;

        const string sha1 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        Store(sha1, "copied");

        string target = Path.Combine(_Root, "roms");

        Place.PlaceAssetFiles(
            Assets(("machine.zip", sha1)),
            _HashStore,
            target,
            null,
            new[] { "MachineRom", "mame", "a2600" });

        string placed = Path.Combine(target, "machine.zip");

        Assert.True(File.Exists(placed));
        Assert.Null(new FileInfo(placed).ResolveLinkTarget(true));
        Assert.Equal("copied", File.ReadAllText(placed));
    }
}
