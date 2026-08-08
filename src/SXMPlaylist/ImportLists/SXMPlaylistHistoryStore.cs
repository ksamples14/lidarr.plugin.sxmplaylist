using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NzbDrone.Common.EnvironmentInfo;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// Persistent play history, shared across all SXMPlaylist lists in this Lidarr instance.
    /// Backed by SQLite via the same System.Data.SQLite assembly Lidarr itself ships (see lib/),
    /// so no extra native binary is bundled with the plugin.
    /// 
    /// The DB is the source of truth for the whole plugin:
    /// - Plays: a rolling record of every play seen, deduped by (PlayId, Artist). Kept for the
    /// retention window as the history source (and future Plex-playlist feature).
    /// - Tracks: one row per xmplaylist track id, holding the resolution inputs (artist(s), song,
    /// Deezer/Apple links), the resolution result (album + MusicBrainz IDs) once resolved, a
    /// strike counter (3 failures = give up), and the resolve time (drives the presentation
    /// time-window).
    /// - ChannelState: when each channel was last captured, so the background worker knows when a
    /// channel is due for its hourly download.
    /// </summary>
    public class SXMPlaylistHistoryStore
    {
        public static readonly TimeSpan PlayRetention = TimeSpan.FromDays(180);
        public static readonly TimeSpan CaptureInterval = TimeSpan.FromHours(1);
        public static readonly TimeSpan PresentationWindow = TimeSpan.FromHours(25);
        public static readonly int MaxResolutionFailures = 3;

        // no-MBID tracks (resolved to a Deezer/Apple title without a MusicBrainz album ID) are
        // re-attempted on a long cadence: many are transient MB-503s that WOULD resolve on a later
        // try, but the 2-retry budget inside a single resolve is exhausted and they're never
        // re-queued otherwise (see PLAN §5.8 / §7 TODO #3).
        public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(12);
        public static readonly int MaxRetryAttempts = 10;

        private readonly string _connectionString;

        public SXMPlaylistHistoryStore(IAppFolderInfo appFolderInfo)
        {
            var folder = Path.Combine(appFolderInfo.AppDataFolder, "SXMPlaylist");
            Directory.CreateDirectory(folder);

            var dbPath = Path.Combine(folder, "history.db");
            _connectionString = $"Data Source={dbPath};Version=3;";

            Initialize();
        }

        private void Initialize()
        {
            using var connection = OpenConnection();

            // WAL lets the background worker write while the import lists read (the lists' Fetch()
            // runs in parallel across channels).
            using (var pragma = new SQLiteCommand("PRAGMA journal_mode=WAL", connection))
            {
                pragma.ExecuteNonQuery();
            }

            using (var pragma = new SQLiteCommand("PRAGMA synchronous=NORMAL", connection))
            {
                pragma.ExecuteNonQuery();
            }

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
                "CREATE TABLE IF NOT EXISTS Tracks (" +
                "TrackId TEXT PRIMARY KEY, " +
                "Channel TEXT NOT NULL, " +
                "ArtistsJson TEXT NOT NULL, " +
                "Song TEXT NOT NULL, " +
                "DeezerUrl TEXT, " +
                "AppleMusicUrl TEXT, " +
                "TimestampUtc TEXT NOT NULL, " +
                "Resolved INTEGER NOT NULL DEFAULT 0, " +
                "Failures INTEGER NOT NULL DEFAULT 0, " +
                "Album TEXT, " +
                "ArtistMusicBrainzId TEXT, " +
                "AlbumMusicBrainzId TEXT, " +
                "ResolvedUtc TEXT, " +
                "NextRetryUtc TEXT, " +
                "RetryAttempts INTEGER NOT NULL DEFAULT 0)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            // Upgrade databases created before the retry columns existed (the live DB has thousands
            // of rows). Idempotent: skips when the column already exists, safe to run every start.
            var addedNextRetry = EnsureColumn(connection, "Tracks", "NextRetryUtc", "TEXT");
            var addedRetryAttempts = EnsureColumn(connection, "Tracks", "RetryAttempts", "INTEGER NOT NULL DEFAULT 0");

            // Only when this is a genuine first migration (a column was just added): historical
            // no-MBID rows have NextRetryUtc = NULL, which GetDueRetries would treat as immediately
            // due. Stagger the whole backlog out by a full interval so the rollout doesn't re-hit
            // MusicBrainz for thousands of tracks at once. Fresh plays legitimately reset
            // NextRetryUtc to NULL for an immediate retry; that path is NOT touched here because
            // this only runs on the start that performed the migration.
            if (addedNextRetry || addedRetryAttempts)
            {
                using var backfill = new SQLiteCommand(
                    "UPDATE Tracks SET NextRetryUtc = @staggeredRetry, RetryAttempts = 1 " +
                    "WHERE Resolved = 1 AND AlbumMusicBrainzId IS NULL AND NextRetryUtc IS NULL",
                    connection);
                backfill.Parameters.AddWithValue("@staggeredRetry", DateTime.UtcNow.Add(RetryInterval).ToString("O"));
                backfill.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Tracks_Channel_Resolved ON Tracks (Channel, Resolved)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS ChannelState (" +
                "Channel TEXT PRIMARY KEY, " +
                "LastCaptureUtc TEXT NOT NULL)",
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

        // Adds a column to an existing table if it isn't present. Used to upgrade databases created
        // before a column existed; safe to call on every Initialize() (idempotent, metadata-only).
        // Returns true when the column was actually added (i.e. this was a real migration).
        private static bool EnsureColumn(SQLiteConnection connection, string table, string column, string definition)
        {
            using var check = new SQLiteCommand($"PRAGMA table_info({table})", connection);
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            using var alter = new SQLiteCommand($"ALTER TABLE {table} ADD COLUMN {column} {definition}", connection);
            alter.ExecuteNonQuery();
            return true;
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

        // Records (or refreshes) the resolution inputs for a track. Called when a play is first seen;
        // an already-present row keeps its resolution/failure state and just gets its latest play
        // timestamp, song and links updated.
        public void UpsertTrack(
            string trackId,
            string channel,
            IReadOnlyList<string> artists,
            string song,
            string? deezerUrl,
            string? appleMusicUrl,
            DateTime timestampUtc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT INTO Tracks (TrackId, Channel, ArtistsJson, Song, DeezerUrl, AppleMusicUrl, TimestampUtc) " +
                "VALUES (@trackId, @channel, @artists, @song, @deezerUrl, @appleMusicUrl, @timestamp) " +
                "ON CONFLICT(TrackId) DO UPDATE SET Channel = @channel, ArtistsJson = @artists, Song = @song, " +
                "DeezerUrl = @deezerUrl, AppleMusicUrl = @appleMusicUrl, TimestampUtc = @timestamp, " +
                "NextRetryUtc = CASE WHEN AlbumMusicBrainzId IS NULL THEN NULL ELSE NextRetryUtc END, " +
                "RetryAttempts = CASE WHEN AlbumMusicBrainzId IS NULL THEN 0 ELSE RetryAttempts END",
                connection);

            command.Parameters.AddWithValue("@trackId", trackId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artists", JsonConvert.SerializeObject(artists));
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@deezerUrl", (object?)deezerUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@appleMusicUrl", (object?)appleMusicUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            command.ExecuteNonQuery();
        }

        // Tracks that still need resolution and haven't exhausted their retries, oldest first.
        public IReadOnlyList<PendingTrack> GetDueTracks(int limit)
        {
            var results = new List<PendingTrack>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT TrackId, Channel, ArtistsJson, Song, DeezerUrl, AppleMusicUrl FROM Tracks " +
                "WHERE Resolved = 0 AND Failures < @maxFailures ORDER BY TimestampUtc ASC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@maxFailures", MaxResolutionFailures);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PendingTrack(
                    reader.GetString(0),
                    reader.GetString(1),
                    JsonConvert.DeserializeObject<List<string>>(reader.GetString(2)) ?? new List<string>(),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return results;
        }

        public void MarkTrackResolved(string trackId, AlbumResolution resolution, DateTime? resolvedUtc = null)
        {
            var resolvedAt = resolvedUtc ?? DateTime.UtcNow;

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET Resolved = 1, Failures = 0, Album = @album, " +
                "ArtistMusicBrainzId = @artistMbid, AlbumMusicBrainzId = @albumMbid, ResolvedUtc = @resolvedUtc, " +
                "RetryAttempts = 0, " +
                "NextRetryUtc = CASE WHEN @albumMbid IS NULL THEN @nextRetry ELSE NULL END " +
                "WHERE TrackId = @trackId",
                connection);

            command.Parameters.AddWithValue("@album", (object?)resolution.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("@artistMbid", (object?)resolution.ArtistMusicBrainzId ?? DBNull.Value);
            command.Parameters.AddWithValue("@albumMbid", (object?)resolution.AlbumMusicBrainzId ?? DBNull.Value);
            command.Parameters.AddWithValue("@resolvedUtc", resolvedAt.ToString("O"));
            command.Parameters.AddWithValue("@nextRetry", resolvedAt.Add(RetryInterval).ToString("O"));
            command.Parameters.AddWithValue("@trackId", trackId);

            command.ExecuteNonQuery();
        }

        // no-MBID tracks that have used some of their resolution retry budget and are due for the
        // next attempt, oldest-scheduled first. Kept separate from GetDueTracks (never-resolved
        // tracks) so first-time resolution is never starved by the retry backlog.
        public IReadOnlyList<PendingTrack> GetDueRetries(int limit, DateTime nowUtc)
        {
            var results = new List<PendingTrack>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT TrackId, Channel, ArtistsJson, Song, DeezerUrl, AppleMusicUrl FROM Tracks " +
                "WHERE Resolved = 1 AND AlbumMusicBrainzId IS NULL AND RetryAttempts < @maxRetryAttempts " +
                "AND (NextRetryUtc IS NULL OR NextRetryUtc <= @now) " +
                "ORDER BY NextRetryUtc ASC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@maxRetryAttempts", MaxRetryAttempts);
            command.Parameters.AddWithValue("@now", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PendingTrack(
                    reader.GetString(0),
                    reader.GetString(1),
                    JsonConvert.DeserializeObject<List<string>>(reader.GetString(2)) ?? new List<string>(),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return results;
        }

        // A failed retry attempt on a no-MBID track: bumps the attempt counter, schedules the next
        // try 12h out, and renews ResolvedUtc so the track stays presentable through the retry
        // window. Give-up is implicit - GetDueRetries stops selecting once RetryAttempts hits max.
        public void RecordRetryFailure(string trackId, DateTime nowUtc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET RetryAttempts = RetryAttempts + 1, " +
                "NextRetryUtc = @next, ResolvedUtc = @now WHERE TrackId = @trackId",
                connection);

            command.Parameters.AddWithValue("@next", nowUtc.Add(RetryInterval).ToString("O"));
            command.Parameters.AddWithValue("@now", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("@trackId", trackId);

            command.ExecuteNonQuery();
        }

        public void RecordTrackFailure(string trackId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET Failures = Failures + 1 WHERE TrackId = @trackId",
                connection);
            command.Parameters.AddWithValue("@trackId", trackId);
            command.ExecuteNonQuery();
        }

        // Tracks resolved within the presentation window, for this channel - i.e. what the import
        // list hands to Lidarr. Resolved tracks stay presentable for the window; Lidarr's own
        // import-list processing is idempotent, so re-presentation is harmless.
        public IReadOnlyList<PresentableTrack> GetPresentableTracks(string channel, DateTime resolvedSinceUtc, int limit, IReadOnlyList<ShowWindow>? windows = null)
        {
            var results = new List<PresentableTrack>();
            var windowFilter = "";
            if (windows != null)
            {
                if (windows.Count == 0)
                {
                    return results;
                }

                windowFilter = " AND (" + string.Join(" OR ", windows.Select((_, i) => $"(TimestampUtc >= @windowStart{i} AND TimestampUtc < @windowEnd{i})")) + ")";
            }

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT TrackId, ArtistsJson, Song, Album, ArtistMusicBrainzId, AlbumMusicBrainzId, TimestampUtc FROM Tracks " +
                "WHERE Channel = @channel AND Resolved = 1 AND ResolvedUtc >= @since" + windowFilter + " ORDER BY ResolvedUtc DESC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@since", resolvedSinceUtc.ToString("O"));
            command.Parameters.AddWithValue("@limit", limit);
            if (windows != null)
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    command.Parameters.AddWithValue($"@windowStart{i}", windows[i].StartUtc.ToString("O"));
                    command.Parameters.AddWithValue($"@windowEnd{i}", windows[i].EndUtc.ToString("O"));
                }
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var timestampUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime();
                results.Add(new PresentableTrack(
                    reader.GetString(0),
                    JsonConvert.DeserializeObject<List<string>>(reader.GetString(1)) ?? new List<string>(),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    timestampUtc));
            }

            return results;
        }

        public DateTime? GetLastCaptureUtc(string channel)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand("SELECT LastCaptureUtc FROM ChannelState WHERE Channel = @channel", connection);
            command.Parameters.AddWithValue("@channel", channel);

            var result = command.ExecuteScalar();
            return result == null || result is DBNull ? (DateTime?)null : DateTime.Parse((string)result).ToUniversalTime();
        }

        public void SetLastCaptureUtc(string channel, DateTime utc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT INTO ChannelState (Channel, LastCaptureUtc) VALUES (@channel, @utc) " +
                "ON CONFLICT(Channel) DO UPDATE SET LastCaptureUtc = @utc",
                connection);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@utc", utc.ToString("O"));
            command.ExecuteNonQuery();
        }

        // Rolls the history forward: drops plays and tracks that have fallen out of the retention
        // window. Runs from the background worker, not from Fetch().
        public void Prune()
        {
            var cutoff = DateTime.UtcNow - PlayRetention;

            using var connection = OpenConnection();
            using (var command = new SQLiteCommand("DELETE FROM Plays WHERE TimestampUtc < @cutoff", connection))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand("DELETE FROM Tracks WHERE TimestampUtc < @cutoff", connection))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }
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

    public class PendingTrack
    {
        public PendingTrack(string trackId, string channel, IReadOnlyList<string> artists, string song, string? deezerUrl, string? appleMusicUrl)
        {
            TrackId = trackId;
            Channel = channel;
            Artists = artists;
            Song = song;
            DeezerUrl = deezerUrl;
            AppleMusicUrl = appleMusicUrl;
        }

        public string TrackId { get; }
        public string Channel { get; }
        public IReadOnlyList<string> Artists { get; }
        public string Song { get; }
        public string? DeezerUrl { get; }
        public string? AppleMusicUrl { get; }
    }

    public class PresentableTrack
    {
        public PresentableTrack(string trackId, IReadOnlyList<string> artists, string song, string? album, string? artistMusicBrainzId, string? albumMusicBrainzId, DateTime timestampUtc)
        {
            TrackId = trackId;
            Artists = artists;
            Song = song;
            Album = album;
            ArtistMusicBrainzId = artistMusicBrainzId;
            AlbumMusicBrainzId = albumMusicBrainzId;
            TimestampUtc = timestampUtc;
        }

        public string TrackId { get; }
        public IReadOnlyList<string> Artists { get; }
        public string Song { get; }
        public string? Album { get; }
        public string? ArtistMusicBrainzId { get; }
        public string? AlbumMusicBrainzId { get; }
        public DateTime TimestampUtc { get; }
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
