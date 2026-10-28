using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Spludlow.MameAO;

namespace MameAo.Tests;

public class HashStoreTests : IDisposable
{
    private readonly string _Root;
    private readonly string _Store;

    public HashStoreTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "mame-ao-store-" + Guid.NewGuid().ToString("N"));
        _Store = Path.Combine(_Root, "store");
        Directory.CreateDirectory(_Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_Root, true); } catch (Exception) { }
    }

    private static string Sha1(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private string WriteFile(string name, string content)
    {
        string filename = Path.Combine(_Root, name);
        File.WriteAllText(filename, content, Encoding.UTF8);
        return filename;
    }

    [Fact]
    public void GetFileName_UsesTwoCharacterPrefixAndCombinesProperly()
    {
        string filename = HashStore.GetFileName("/store", "ABCDEF0123456789");

        Assert.Equal(Path.Combine("/store", "ab", "abcdef0123456789"), filename);
        Assert.DoesNotContain('\\', filename);
    }

    [Fact]
    public void Add_CopiesAndIndexesByHash()
    {
        HashStore store = new HashStore(_Store, Sha1);

        string source = WriteFile("a.bin", "hello");
        string hash = Sha1(source);

        Assert.True(store.Add(source));
        Assert.True(File.Exists(source));
        Assert.True(store.Exists(hash));
        Assert.True(store.Exists(hash.ToUpperInvariant()));
        Assert.Equal(hash, Path.GetFileName(store.Filename(hash)));

        // Adding the same content again is a no-op.
        Assert.False(store.Add(source));
        Assert.Equal(1, store.Length);
    }

    [Fact]
    public void Add_MoveRemovesSource()
    {
        HashStore store = new HashStore(_Store, Sha1);

        string source = WriteFile("b.bin", "move me");

        Assert.True(store.Add(source, move: true));
        Assert.False(File.Exists(source));
        Assert.Equal(1, store.Length);
    }

    [Fact]
    public void Add_WithKnownHashSkipsHashing()
    {
        bool hashed = false;

        HashStore store = new HashStore(_Store, _ => { hashed = true; return "unused"; });

        string source = WriteFile("c.bin", "known");
        const string hash = "0123456789abcdef0123456789abcdef01234567";

        Assert.True(store.Add(source, move: false, sha1: hash));
        Assert.False(hashed);
        Assert.True(store.Exists(hash));
    }

    [Fact]
    public void Refresh_FindsExistingEntriesAfterRestart()
    {
        string source = WriteFile("d.bin", "persisted");
        string hash = Sha1(source);

        new HashStore(_Store, Sha1).Add(source);

        HashStore reopened = new HashStore(_Store, Sha1);

        Assert.True(reopened.Exists(hash));
        Assert.Equal(1, reopened.Length);
    }

    [Fact]
    public void Delete_RemovesFileAndIndex()
    {
        HashStore store = new HashStore(_Store, Sha1);

        string source = WriteFile("e.bin", "delete me");
        string hash = Sha1(source);

        store.Add(source);
        string stored = store.Filename(hash);

        Assert.True(store.Delete(hash));
        Assert.False(store.Exists(hash));
        Assert.False(File.Exists(stored));
        Assert.False(store.Delete(hash));
    }

    [Fact]
    public void Exists_RejectsEmptyHash()
    {
        HashStore store = new HashStore(_Store, Sha1);

        Assert.False(store.Exists(null));
        Assert.False(store.Exists(""));
        Assert.Null(store.Filename(null));
    }
}
