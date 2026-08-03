using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace SatList.ImportLists
{
    public class SatListParser : IParseImportListResponse
    {
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
                var entries = JsonConvert.DeserializeObject<List<SatListEntry>>(_importListResponse.Content);

                if (entries == null)
                {
                    return items;
                }

                foreach (var entry in entries)
                {
                    if (entry.Artist.IsNullOrWhiteSpace() &&
                        entry.Album.IsNullOrWhiteSpace() &&
                        entry.ArtistMusicBrainzId.IsNullOrWhiteSpace() &&
                        entry.AlbumMusicBrainzId.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    items.Add(new ImportListItemInfo
                    {
                        Artist = entry.Artist,
                        ArtistMusicBrainzId = entry.ArtistMusicBrainzId,
                        Album = entry.Album,
                        AlbumMusicBrainzId = entry.AlbumMusicBrainzId,
                        ReleaseDate = entry.ReleaseDate != default ? entry.ReleaseDate : (DateTime?)null
                    });
                }
            }
            catch (Exception ex)
            {
                throw new ImportListException(_importListResponse, "Failed to parse import list JSON: {0}", ex.Message);
            }

            return items;
        }

        protected virtual bool PreProcess(ImportListResponse importListResponse)
        {
            if (importListResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new ImportListException(
                    importListResponse,
                    "API call returned status {0}",
                    importListResponse.HttpResponse.StatusCode);
            }

            if (importListResponse.Content.IsNullOrWhiteSpace())
            {
                throw new ImportListException(importListResponse, "API returned empty response");
            }

            return true;
        }
    }

    internal class SatListEntry
    {
        public string Artist { get; set; }
        public string ArtistMusicBrainzId { get; set; }
        public string Album { get; set; }
        public string AlbumMusicBrainzId { get; set; }
        public DateTime ReleaseDate { get; set; }
    }
}
