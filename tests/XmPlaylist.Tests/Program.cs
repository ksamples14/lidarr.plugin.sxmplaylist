using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser.Model;
using XmPlaylist.ImportLists;

internal static class Program
{
    private static int _failures;

    public static void Main()
    {
        Console.WriteLine("=== XmPlaylist Parser Tests ===");

        var stateFolder = Path.Combine(Path.GetTempPath(), "xmplaylist-test-state");
        if (Directory.Exists(stateFolder))
        {
            Directory.Delete(stateFolder, true);
        }

        var disk = new FakeDiskProvider();
        var appFolder = new FakeAppFolderInfo(stateFolder);
        var store = new XmPlaylistStateStore(disk, appFolder);

        TestDiffing(store);
        TestChannelFilter(store);
        TestImportTypes(store);
        TestStatePersistence(store);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void TestDiffing(XmPlaylistStateStore store)
    {
        Console.WriteLine("\n[Test] OnlyNewArtists diffing");

        var settings = new XmPlaylistImportSettings
        {
            OnlyNewArtists = true,
            DedupeArtists = true,
            ImportType = (int)XmPlaylistImportType.Artists
        };

        var parser = new XmPlaylistParser { Settings = settings, StateStore = store, ListId = 1 };
        var feed = BuildFeed(
            ("Artist One", "altnation"),
            ("Artist Two", "xmu"),
            ("Artist One", "altnation"));

        var first = parser.ParseResponse(feed);
        Assert($"first fetch has 2 unique artists", first.Count == 2);
        Assert("first fetch contains Artist One", ContainsArtist(first, "Artist One"));
        Assert("first fetch contains Artist Two", ContainsArtist(first, "Artist Two"));

        var second = parser.ParseResponse(feed);
        Assert($"second fetch emits nothing new (got {second.Count})", second.Count == 0);
    }

    private static void TestChannelFilter(XmPlaylistStateStore store)
    {
        Console.WriteLine("\n[Test] Channel filter (client-side)");

        var settings = new XmPlaylistImportSettings
        {
            OnlyNewArtists = false,
            DedupeArtists = true,
            ImportType = (int)XmPlaylistImportType.Artists,
            ChannelFilter = "altnation"
        };

        var parser = new XmPlaylistParser { Settings = settings, StateStore = store, ListId = 2 };
        var feed = BuildFeed(
            ("Artist One", "altnation"),
            ("Artist Two", "xmu"),
            ("Artist Three", "altnation"));

        var items = parser.ParseResponse(feed);
        Assert($"channel filter keeps only altnation (got {items.Count})", items.Count == 2);
        Assert("no xmu artist leaks through", !ContainsArtist(items, "Artist Two"));
    }

    private static void TestImportTypes(XmPlaylistStateStore store)
    {
        Console.WriteLine("\n[Test] Import types");

        var settings = new XmPlaylistImportSettings
        {
            OnlyNewArtists = false,
            DedupeArtists = true,
            ImportType = (int)XmPlaylistImportType.Albums
        };

        var parser = new XmPlaylistParser { Settings = settings, StateStore = store, ListId = 3 };
        var feed = BuildFeed(
            ("Artist One", "altnation"),
            ("Artist Two", "xmu"));

        var items = parser.ParseResponse(feed);
        Assert($"albums mode produces 2 items (got {items.Count})", items.Count == 2);
        Assert("album item has Album set", items[0].Album.IsNotNullOrWhiteSpace());
    }

    private static void TestStatePersistence(XmPlaylistStateStore store)
    {
        Console.WriteLine("\n[Test] State persists across parser instances");

        var settings = new XmPlaylistImportSettings
        {
            OnlyNewArtists = true,
            DedupeArtists = true,
            ImportType = (int)XmPlaylistImportType.Artists
        };

        var parser1 = new XmPlaylistParser { Settings = settings, StateStore = store, ListId = 4 };
        var feed = BuildFeed(("Artist Persisted", "altnation"));
        parser1.ParseResponse(feed);

        var parser2 = new XmPlaylistParser { Settings = settings, StateStore = store, ListId = 4 };
        var second = parser2.ParseResponse(feed);
        Assert($"new parser instance sees prior state (got {second.Count})", second.Count == 0);
    }

    private static ImportListResponse BuildFeed(params (string Artist, string Channel)[] plays)
    {
        var entries = new List<string>();
        foreach (var play in plays)
        {
            entries.Add($"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":\"2026-08-04T00:00:00Z\",\"track\":{{\"id\":\"T\",\"title\":\"Song\",\"artists\":[\"{play.Artist}\"]}},\"channelId\":\"{play.Channel}\"}}");
        }

        var json = "{\"count\":" + entries.Count + ",\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", entries) + "]}";

        var request = new HttpRequest("https://xmplaylist.com/api/feed", HttpAccept.Json);
        var importRequest = new ImportListRequest(request);
        var httpResponse = new HttpResponse(importRequest.HttpRequest, new HttpHeader(), json, HttpStatusCode.OK);
        return new ImportListResponse(importRequest, httpResponse);
    }

    private static bool ContainsArtist(IList<ImportListItemInfo> items, string artist)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Artist, artist, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Assert(string description, bool condition)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {description}");
        if (!condition)
        {
            _failures++;
        }
    }
}

internal class FakeDiskProvider : IDiskProvider
{
    private readonly Dictionary<string, string> _files = new();

    public string ReadAllText(string filePath) => _files.TryGetValue(filePath, out var v) ? v : string.Empty;
    public void WriteAllText(string filename, string contents) => _files[filename] = contents;
    public bool FileExists(string path) => _files.ContainsKey(path);
    public void EnsureFolder(string path) { }
    public bool FolderExists(string path) => true;
    public long? GetAvailableSpace(string path) => null;
    public void InheritFolderPermissions(string filename) { }
    public void SetEveryonePermissions(string filename) { }
    public void SetFilePermissions(string path, string mask, string group) { }
    public void SetPermissions(string path, string mask, string group) { }
    public void CopyPermissions(string sourcePath, string targetPath) { }
    public long? GetTotalSize(string path) => null;
    public DateTime FolderGetCreationTime(string path) => DateTime.MinValue;
    public DateTime FolderGetLastWrite(string path) => DateTime.MinValue;
    public DateTime FileGetLastWrite(string path) => DateTime.MinValue;
    public bool FileExists(string path, StringComparison stringComparison) => false;
    public bool FolderWritable(string path) => true;
    public bool FolderEmpty(string path) => false;
    public IEnumerable<string> GetDirectories(string path) => new List<string>();
    public IEnumerable<string> GetFiles(string path, bool recursive) => new List<string>();
    public long GetFolderSize(string path) => 0;
    public long GetFileSize(string path) => 0;
    public void CreateFolder(string path) { }
    public void DeleteFile(string path) { }
    public void CloneFile(string source, string destination, bool overwrite = false) { }
    public void CopyFile(string source, string destination, bool overwrite = false) { }
    public void MoveFile(string source, string destination, bool overwrite = false) { }
    public void MoveFolder(string source, string destination) { }
    public bool TryRenameFile(string source, string destination) => false;
    public bool TryCreateHardLink(string source, string destination) => false;
    public bool TryCreateRefLink(string source, string destination) => false;
    public void DeleteFolder(string path, bool recursive) { }
    public void FolderSetLastWriteTime(string path, DateTime dateTime) { }
    public void FileSetLastWriteTime(string path, DateTime dateTime) { }
    public bool IsFileLocked(string path) => false;
    public string GetPathRoot(string path) => Path.GetPathRoot(path) ?? "";
    public string GetParentFolder(string path) => Path.GetDirectoryName(path) ?? "";
    public FileAttributes GetFileAttributes(string path) => FileAttributes.Normal;
    public void EmptyFolder(string path) { }
    public string GetVolumeLabel(string path) => "";
    public FileStream OpenReadStream(string path) => throw new NotSupportedException();
    public FileStream OpenWriteStream(string path) => throw new NotSupportedException();
    public List<IMount> GetMounts() => new();
    public IMount? GetMount(string path) => null;
    public System.IO.Abstractions.IDirectoryInfo GetDirectoryInfo(string path) => throw new NotSupportedException();
    public List<System.IO.Abstractions.IDirectoryInfo> GetDirectoryInfos(string path) => new();
    public System.IO.Abstractions.IFileInfo GetFileInfo(string path) => throw new NotSupportedException();
    public List<System.IO.Abstractions.IFileInfo> GetFileInfos(string path, bool recursive = false) => new();
    public void RemoveEmptySubfolders(string path) { }
    public void SaveStream(Stream stream, string path) { }
    public bool IsValidFolderPermissionMask(string mask) => false;
}

internal class FakeAppFolderInfo : IAppFolderInfo
{
    public FakeAppFolderInfo(string appDataFolder)
    {
        AppDataFolder = appDataFolder;
    }

    public string AppDataFolder { get; }
    public string TempFolder => Path.GetTempPath();
    public string StartUpFolder => AppDomain.CurrentDomain.BaseDirectory;
}
