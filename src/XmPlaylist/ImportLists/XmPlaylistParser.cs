using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistParser : IParseImportListResponse
    {
        // MusicBrainz's own rate limit (1 req/sec, up to 2 calls per newly-seen song) means a
        // fetch with many brand-new songs - e.g. a first-ever Test that just backfilled a full
        // 6-hour window - could otherwise spend minutes doing sequential album lookups before
        // Lidarr ever gets a response back. Cap it so a fetch always returns promptly; whatever
        // didn't get resolved in time is imported artist-only and picked up fresh on a later poll
        // (nothing is cached for a skipped attempt, so it isn't treated as a permanent failure).
        public TimeSpan AlbumResolutionBudget { get; set; } = TimeSpan.FromSeconds(20);

        public XmPlaylistImportSettings? Settings { get; set; }

        public XmPlaylistHistoryStore? HistoryStore { get; set; }

        public XmPlaylistAlbumResolver? AlbumResolver { get; set; }

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private ImportListResponse _importListResponse = null!;

        public IList<ImportListItemInfo> ParseResponse(ImportListResponse importListResponse)
        {
            _importListResponse = importListResponse;

            var items = new List<ImportListItemInfo>();

            if (!PreProcess(_importListResponse))
            {
                return items;
            }

            try
            {
                var feed = JsonConvert.DeserializeObject<XmFeedResponse>(_importListResponse.Content);

                if (feed?.Results == null || feed.Results.Count == 0)
                {
                    return items;
                }

                var channel = Settings?.Channel ?? "";

                foreach (var play in feed.Results)
                {
                    if (play.Id.IsNullOrWhiteSpace() || play.Track?.Artists == null || play.Track.Artists.Count == 0)
                    {
                        continue;
                    }

                    var playChannel = play.ChannelId.IsNotNullOrWhiteSpace() ? play.ChannelId! : channel;
                    var song = play.Track.Title ?? "";

                    var newArtistsThisPlay = new List<string>();

                    foreach (var artist in play.Track.Artists)
                    {
                        if (artist.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        var isNewPlay = HistoryStore?.TryRecordPlay(play.Id!, playChannel, artist, song, play.Timestamp) ?? true;

                        if (isNewPlay)
                        {
                            newArtistsThisPlay.Add(artist);
                        }
                    }

                    if (newArtistsThisPlay.Count == 0)
                    {
                        continue;
                    }

                    var album = ResolveAlbum(play, song);

                    foreach (var artist in newArtistsThisPlay)
                    {
                        // Always emit a plain artist item. Lidarr's own sync deliberately drops the
                        // whole item - artist included - if an Album it was given can't be matched
                        // (ImportListSyncService.MapAlbumReport: "avoid us from adding the artist and
                        // possibly getting it wrong"). Sending the artist on its own too, alongside
                        // whatever album item we can build below, means a failed/rejected album match
                        // never costs us the artist - Lidarr dedupes both back into one artist via its
                        // own MusicBrainz-ID staging, so this doesn't create a duplicate when the album
                        // item succeeds.
                        items.Add(new ImportListItemInfo
                        {
                            Artist = artist,
                            ReleaseDate = play.Timestamp
                        });

                        if (album is { Resolved: true })
                        {
                            var albumItem = new ImportListItemInfo
                            {
                                Artist = artist,
                                Album = album.Album,
                                AlbumMusicBrainzId = album.AlbumMusicBrainzId ?? "",
                                ReleaseDate = play.Timestamp
                            };

                            // Only trust the resolved artist MBID when this play has exactly one
                            // credited artist - a multi-artist (collab) play's single artist-credit
                            // match doesn't reliably map onto every credited artist string.
                            if (newArtistsThisPlay.Count == 1)
                            {
                                albumItem.ArtistMusicBrainzId = album.ArtistMusicBrainzId ?? "";
                            }

                            items.Add(albumItem);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ImportListException(_importListResponse, "Failed to parse xmplaylist feed: {0}", ex.Message);
            }

            return items;
        }

        private AlbumResolution? ResolveAlbum(XmPlayEntry play, string song)
        {
            var trackId = play.Track?.Id;
            if (trackId.IsNullOrWhiteSpace() || AlbumResolver == null)
            {
                return null;
            }

            var cached = HistoryStore?.GetCachedAlbumResolution(trackId!);
            if (cached != null)
            {
                return cached;
            }

            if (_stopwatch.Elapsed > AlbumResolutionBudget)
            {
                return null;
            }

            var links = play.Links?
                .Where(l => l.Site.IsNotNullOrWhiteSpace() && l.Url.IsNotNullOrWhiteSpace())
                .GroupBy(l => l.Site!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Url!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>();

            var artist = play.Track?.Artists?.FirstOrDefault() ?? "";
            var resolution = AlbumResolver.Resolve(artist, song, links);

            HistoryStore?.CacheAlbumResolution(trackId!, resolution);

            return resolution;
        }

        protected virtual bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(
                    importListResponse,
                    "xmplaylist API returned status {0}",
                    importListResponse.HttpResponse.StatusCode);
            }

            if (importListResponse.Content.IsNullOrWhiteSpace())
            {
                throw new ImportListException(importListResponse, "xmplaylist API returned empty response");
            }

            return true;
        }
    }

    internal class XmFeedResponse
    {
        public int Count { get; set; }
        public List<XmPlayEntry>? Results { get; set; }
    }

    internal class XmPlayEntry
    {
        public string? Id { get; set; }
        public DateTime Timestamp { get; set; }
        public XmTrackInfo? Track { get; set; }
        [JsonProperty("channelId")]
        public string? ChannelId { get; set; }
        public List<XmLink>? Links { get; set; }
    }

    internal class XmTrackInfo
    {
        public string? Id { get; set; }
        public List<string>? Artists { get; set; }
        public string? Title { get; set; }
    }

    internal class XmLink
    {
        public string? Site { get; set; }
        public string? Url { get; set; }
    }
}
