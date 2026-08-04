using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using NzbDrone.Common.EnvironmentInfo;

namespace XmPlaylist.ImportLists
{
    // Persistent play history, shared across all XmPlaylist lists in this Lidarr instance.
    // Backed by SQLite via the same System.Data.SQLite assembly Lidarr itself ships (see lib/),
    // so no extra native binary is bundled with the plugin.
    //
    // Doubles as the dedup mechanism: a play is only forwarded to Lidarr the first time its
    // (PlayId, Artist) pair is recorded, so re-fetching an overlapping backfill window on the
    // next poll doesn't re-emit the same play. It's also the source of truth for a future
    // "build a Plex playlist per station" feature, which needs artist/song/date/time history,
    // not just "have we seen this artist before."
    public class XmPlaylistHistoryStore
    {
        private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
        private static readonly TimeSpan NegativeResolutionRetry = TimeSpan.FromDays(7);

        private readonly string _connectionString;

        public XmPlaylistHistoryStore(IAppFolderInfo appFolderInfo)
        {
            var folder = Path.Combine(appFolderInfo.AppDataFolder, "XmPlaylist");
            Directory.CreateDirectory(folder);

            var dbPath = Path.Combine(folder, "history.db");
            _connectionString = $"Data Source={dbPath};Version=3;";

            Initialize();
        }

        private void Initialize()
        {
            using var connection = OpenConnection();

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS Plays (" +
                "PlayId TEXT NOT NULL, " +
                "Channel TEXT NOT NULL, " +
                "Artist TEXT NOT NULL, " +
                "Song TEXT NOT NULL, " +
                "TimestampUtc TEXT NOT NULL, " +
                "PRIMARY KEY (PlayId, Artist))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Plays_Channel_TimestampUtc ON Plays (Channel, TimestampUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS AlbumResolutions (" +
                "TrackId TEXT PRIMARY KEY, " +
                "Resolved INTEGER NOT NULL, " +
                "Album TEXT, " +
                "ArtistMusicBrainzId TEXT, " +
                "AlbumMusicBrainzId TEXT, " +
                "ResolvedUtc TEXT NOT NULL)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS Channels (" +
                "Deeplink TEXT PRIMARY KEY, " +
                "Name TEXT NOT NULL, " +
                "Number TEXT, " +
                "CachedUtc TEXT NOT NULL)",
                connection))
            {
                command.ExecuteNonQuery();
            }
        }

        // Returns true if this (play, artist) pair was newly recorded, false if already known.
        public bool TryRecordPlay(string playId, string channel, string artist, string song, DateTime timestampUtc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT OR IGNORE INTO Plays (PlayId, Channel, Artist, Song, TimestampUtc) VALUES (@playId, @channel, @artist, @song, @timestamp)",
                connection);

            command.Parameters.AddWithValue("@playId", playId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artist", artist);
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            return command.ExecuteNonQuery() > 0;
        }

        public void PruneOlderThan(DateTime cutoffUtc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand("DELETE FROM Plays WHERE TimestampUtc < @cutoff", connection);
            command.Parameters.AddWithValue("@cutoff", cutoffUtc.ToString("O"));
            command.ExecuteNonQuery();
        }

        public void PruneOldPlays()
        {
            PruneOlderThan(DateTime.UtcNow - Retention);

            // Retry failed album lookups periodically (the song may show up on Deezer/Apple later);
            // successful resolutions never change, so those are kept for the full retention window.
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "DELETE FROM AlbumResolutions WHERE Resolved = 0 AND ResolvedUtc < @cutoff", connection);
            command.Parameters.AddWithValue("@cutoff", (DateTime.UtcNow - NegativeResolutionRetry).ToString("O"));
            command.ExecuteNonQuery();
        }

        // Cached by xmplaylist's own per-song track id, not per play: the same song replays
        // constantly on a rotation-heavy station and its album never changes between plays.
        public AlbumResolution? GetCachedAlbumResolution(string trackId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT Resolved, Album, ArtistMusicBrainzId, AlbumMusicBrainzId FROM AlbumResolutions WHERE TrackId = @trackId",
                connection);
            command.Parameters.AddWithValue("@trackId", trackId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AlbumResolution(
                reader.GetInt64(0) != 0,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        public void CacheAlbumResolution(string trackId, AlbumResolution resolution)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT OR REPLACE INTO AlbumResolutions (TrackId, Resolved, Album, ArtistMusicBrainzId, AlbumMusicBrainzId, ResolvedUtc) " +
                "VALUES (@trackId, @resolved, @album, @artistMbid, @albumMbid, @resolvedUtc)",
                connection);

            command.Parameters.AddWithValue("@trackId", trackId);
            command.Parameters.AddWithValue("@resolved", resolution.Resolved ? 1 : 0);
            command.Parameters.AddWithValue("@album", (object?)resolution.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("@artistMbid", (object?)resolution.ArtistMusicBrainzId ?? DBNull.Value);
            command.Parameters.AddWithValue("@albumMbid", (object?)resolution.AlbumMusicBrainzId ?? DBNull.Value);
            command.Parameters.AddWithValue("@resolvedUtc", DateTime.UtcNow.ToString("O"));

            command.ExecuteNonQuery();
        }

        public IReadOnlyList<PlayRecord> GetPlays(string channel, DateTime sinceUtc)
        {
            var results = new List<PlayRecord>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT Artist, Song, TimestampUtc FROM Plays WHERE Channel = @channel AND TimestampUtc >= @since ORDER BY TimestampUtc",
                connection);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@since", sinceUtc.ToString("O"));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PlayRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTime.Parse(reader.GetString(2)).ToUniversalTime()));
            }

            return results;
        }

        // Null if the channel list has never been cached.
        public DateTime? GetChannelCacheAge()
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand("SELECT MAX(CachedUtc) FROM Channels", connection);

            var result = command.ExecuteScalar();
            return result == null || result is DBNull ? (DateTime?)null : DateTime.Parse((string)result).ToUniversalTime();
        }

        public IReadOnlyList<ChannelInfo> GetCachedChannels()
        {
            var results = new List<ChannelInfo>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand("SELECT Deeplink, Name, Number FROM Channels ORDER BY Name", connection);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ChannelInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return results;
        }

        public void SaveChannels(IEnumerable<ChannelInfo> channels)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var clear = new SQLiteCommand("DELETE FROM Channels", connection, transaction))
            {
                clear.ExecuteNonQuery();
            }

            var cachedUtc = DateTime.UtcNow.ToString("O");

            foreach (var channel in channels)
            {
                using var insert = new SQLiteCommand(
                    "INSERT INTO Channels (Deeplink, Name, Number, CachedUtc) VALUES (@deeplink, @name, @number, @cachedUtc)",
                    connection,
                    transaction);

                insert.Parameters.AddWithValue("@deeplink", channel.Deeplink);
                insert.Parameters.AddWithValue("@name", channel.Name);
                insert.Parameters.AddWithValue("@number", (object?)channel.Number ?? DBNull.Value);
                insert.Parameters.AddWithValue("@cachedUtc", cachedUtc);

                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            return connection;
        }
    }

    public class PlayRecord
    {
        public PlayRecord(string artist, string song, DateTime timestampUtc)
        {
            Artist = artist;
            Song = song;
            TimestampUtc = timestampUtc;
        }

        public string Artist { get; }
        public string Song { get; }
        public DateTime TimestampUtc { get; }
    }

    public class ChannelInfo
    {
        public ChannelInfo(string deeplink, string name, string? number)
        {
            Deeplink = deeplink;
            Name = name;
            Number = number;
        }

        public string Deeplink { get; }
        public string Name { get; }
        public string? Number { get; }
    }

    public class AlbumResolution
    {
        public static readonly AlbumResolution NotFound = new(false, null, null, null);

        public AlbumResolution(bool resolved, string? album, string? artistMusicBrainzId, string? albumMusicBrainzId)
        {
            Resolved = resolved;
            Album = album;
            ArtistMusicBrainzId = artistMusicBrainzId;
            AlbumMusicBrainzId = albumMusicBrainzId;
        }

        public bool Resolved { get; }
        public string? Album { get; }
        public string? ArtistMusicBrainzId { get; }
        public string? AlbumMusicBrainzId { get; }
    }
}
