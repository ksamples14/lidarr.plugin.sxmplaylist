using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistParser : IParseImportListResponse
    {
        public XmPlaylistImportSettings Settings { get; set; }

        private ImportListResponse _importListResponse;

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

                var channelFilter = ParseChannelFilter();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var play in feed.Results)
                {
                    if (play.Track?.Artists == null || play.Track.Artists.Count == 0)
                    {
                        continue;
                    }

                    if (channelFilter.Count > 0 && !channelFilter.Contains(play.ChannelId))
                    {
                        continue;
                    }

                    foreach (var artist in play.Track.Artists)
                    {
                        if (artist.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        if (Settings.DedupeArtists && !seen.Add(artist))
                        {
                            continue;
                        }

                        items.Add(new ImportListItemInfo
                        {
                            Artist = artist,
                            Album = play.Track.Title,
                            ReleaseDate = play.Timestamp
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ImportListException(_importListResponse, "Failed to parse xmplaylist feed: {0}", ex.Message);
            }

            return items;
        }

        private HashSet<string> ParseChannelFilter()
        {
            var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Settings.ChannelFilter.IsNullOrWhiteSpace())
            {
                return filter;
            }

            foreach (var channel in Settings.ChannelFilter.Split(','))
            {
                var trimmed = channel.Trim();
                if (trimmed.IsNotNullOrWhiteSpace())
                {
                    filter.Add(trimmed);
                }
            }

            return filter;
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
        public List<XmPlayEntry> Results { get; set; }
    }

    internal class XmPlayEntry
    {
        public string Id { get; set; }
        public DateTime Timestamp { get; set; }
        public XmTrackInfo Track { get; set; }
        [JsonProperty("channelId")]
        public string ChannelId { get; set; }
    }

    internal class XmTrackInfo
    {
        public string Id { get; set; }
        public List<string> Artists { get; set; }
        public string Title { get; set; }
    }
}
