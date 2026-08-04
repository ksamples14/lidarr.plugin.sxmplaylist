using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistStateStore
    {
        private readonly IDiskProvider _diskProvider;
        private readonly string _stateFolder;

        public XmPlaylistStateStore(IDiskProvider diskProvider, IAppFolderInfo appFolderInfo)
        {
            _diskProvider = diskProvider;
            _stateFolder = Path.Combine(appFolderInfo.AppDataFolder, "XmPlaylist");
        }

        public XmPlaylistState Load(int listId)
        {
            if (listId == 0)
            {
                return new XmPlaylistState();
            }

            var path = GetStatePath(listId);

            if (!_diskProvider.FileExists(path))
            {
                return new XmPlaylistState();
            }

            try
            {
                var json = _diskProvider.ReadAllText(path);
                return JsonConvert.DeserializeObject<XmPlaylistState>(json) ?? new XmPlaylistState();
            }
            catch
            {
                return new XmPlaylistState();
            }
        }

        public void Save(int listId, XmPlaylistState state)
        {
            if (listId == 0)
            {
                return;
            }

            try
            {
                _diskProvider.EnsureFolder(_stateFolder);
                var path = GetStatePath(listId);
                _diskProvider.WriteAllText(path, JsonConvert.SerializeObject(state));
            }
            catch
            {
            }
        }

        private string GetStatePath(int listId)
        {
            return Path.Combine(_stateFolder, $"list-{listId}.json");
        }
    }

    public class XmPlaylistState
    {
        public HashSet<string> SeenArtists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
