using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Commands;
using NzbDrone.Core.Parser.Model;

namespace XmPlaylist.ImportLists
{
    // Lidarr already refresh-scans the artists that a sync *adds*: ImportListSyncService pushes a
    // single BulkRefreshArtistCommand for addedArtists + the artists of addedAlbums. The one gap is
    // an existing artist whose play's album already exists in the DB and is merely being turned on
    // (ProcessAlbumReportForExistingAlbum, "Ensuring Album and Artist monitored") - nothing ever
    // refreshes that artist. This scheduler pushes a RefreshArtistCommand for exactly that case.
    //
    // It runs from within the plugin's Fetch(), which executes inside the ImportListSync command, so
    // the refresh is queued and runs after the sync completes - by which point the album is already
    // monitored, and the refresh + scan picks up that state.
    public class XmPlaylistRefreshScheduler
    {
        private const string VariousArtistsName = "Various Artists";

        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public XmPlaylistRefreshScheduler(
            IArtistService artistService,
            IAlbumService albumService,
            IManageCommandQueue commandQueueManager,
            Logger logger)
        {
            _artistService = artistService;
            _albumService = albumService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Schedule(IList<ImportListItemInfo>? items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var artistIdsToRefresh = new HashSet<int>();

            foreach (var item in items)
            {
                if (item.ArtistMusicBrainzId.IsNullOrWhiteSpace() || item.AlbumMusicBrainzId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var artist = _artistService.FindById(item.ArtistMusicBrainzId);
                if (artist == null || artist.Name.Equals(VariousArtistsName, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var album = _albumService.FindById(item.AlbumMusicBrainzId);
                if (album == null || album.Monitored)
                {
                    continue;
                }

                artistIdsToRefresh.Add(artist.Id);
            }

            if (artistIdsToRefresh.Count == 0)
            {
                return;
            }

            var artistIds = artistIdsToRefresh.ToList();
            _logger.Info("Queueing Refresh & Scan for {0} artist(s) whose album was newly monitored", artistIds.Count);
            _commandQueueManager.Push(new RefreshArtistCommand(artistIds, isNewArtist: false));
        }
    }
}
