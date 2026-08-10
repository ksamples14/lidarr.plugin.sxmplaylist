using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// Persistent play history, shared across all SXMPlaylist lists in this Lidarr instance.
    /// Backed by SQLite via the same System.Data.SQLite assembly Lidarr itself ships (see lib/),
    /// so no extra native binary is bundled with the plugin.
    /// 
    /// The DB is the source of truth for the whole plugin:
    /// - PlayEvents: a rolling record of captured play events, including repeats, with optional
    /// show-window attribution for future playlist generation.
    /// - Plays: legacy/minimum-play source, deduped by (PlayId, Artist), kept during the transition.
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
                "CREATE TABLE IF NOT EXISTS SchemaInfo (Key TEXT PRIMARY KEY, Value TEXT NOT NULL)",
                connection))
            {
                command.ExecuteNonQuery();
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
                "CREATE TABLE IF NOT EXISTS ShowWindows (" +
                "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "Channel TEXT NOT NULL, " +
                "ProgramId TEXT NOT NULL, " +
                "ShowName TEXT NOT NULL, " +
                "StartUtc TEXT NOT NULL, " +
                "EndUtc TEXT NOT NULL, " +
                "CachedUtc TEXT NOT NULL, " +
                "UNIQUE(Channel, ProgramId, StartUtc, EndUtc))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_ShowWindows_Channel_Time ON ShowWindows (Channel, StartUtc, EndUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS PlayEvents (" +
                "PlayEventId INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "PlayId TEXT NOT NULL, " +
                "Channel TEXT NOT NULL, " +
                "TrackId TEXT, " +
                "Artist TEXT NOT NULL, " +
                "Song TEXT NOT NULL, " +
                "TimestampUtc TEXT NOT NULL, " +
                "ShowWindowId INTEGER, " +
                "ProgramId TEXT, " +
                "ShowName TEXT, " +
                "ShowStartUtc TEXT, " +
                "ShowEndUtc TEXT, " +
                "UNIQUE(PlayId, Channel, Artist, Song, TimestampUtc))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_PlayEvents_Channel_TimestampUtc ON PlayEvents (Channel, TimestampUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_PlayEvents_Channel_Program_Time ON PlayEvents (Channel, ProgramId, TimestampUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            if (!GetSchemaFlag(connection, "PlayEventsLegacyMigration"))
            {
                using (var command = new SQLiteCommand(
                    "INSERT OR IGNORE INTO PlayEvents (PlayId, Channel, Artist, Song, TimestampUtc) " +
                    "SELECT PlayId, Channel, Artist, Song, TimestampUtc FROM Plays",
                    connection))
                {
                    command.ExecuteNonQuery();
                }

                SetSchemaFlag(connection, "PlayEventsLegacyMigration");
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
                "CREATE TABLE IF NOT EXISTS TrackResolutions (" +
                "TrackId TEXT NOT NULL, " +
                "ReleasePriority INTEGER NOT NULL, " +
                "Album TEXT, " +
                "ArtistMusicBrainzId TEXT, " +
                "AlbumMusicBrainzId TEXT, " +
                "ResolvedUtc TEXT NOT NULL, " +
                "PRIMARY KEY (TrackId, ReleasePriority))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_TrackResolutions_Priority_Mbid ON TrackResolutions (ReleasePriority, AlbumMusicBrainzId)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            if (!GetSchemaFlag(connection, "TrackResolutionsSinglesMigration"))
            {
                using (var command = new SQLiteCommand(
                    "INSERT OR IGNORE INTO TrackResolutions (TrackId, ReleasePriority, Album, ArtistMusicBrainzId, AlbumMusicBrainzId, ResolvedUtc) " +
                    "SELECT TrackId, @priority, Album, ArtistMusicBrainzId, AlbumMusicBrainzId, ResolvedUtc FROM Tracks " +
                    "WHERE Resolved = 1 AND ResolvedUtc IS NOT NULL",
                    connection))
                {
                    command.Parameters.AddWithValue("@priority", (int)ReleasePriorityMode.Singles);
                    command.ExecuteNonQuery();
                }

                SetSchemaFlag(connection, "TrackResolutionsSinglesMigration");
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

        private static bool GetSchemaFlag(SQLiteConnection connection, string key)
        {
            using var command = new SQLiteCommand("SELECT Value FROM SchemaInfo WHERE Key = @key", connection);
            command.Parameters.AddWithValue("@key", key);
            var result = command.ExecuteScalar();
            return result is string value && string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static void SetSchemaFlag(SQLiteConnection connection, string key)
        {
            using var command = new SQLiteCommand(
                "INSERT INTO SchemaInfo (Key, Value) VALUES (@key, '1') " +
                "ON CONFLICT(Key) DO UPDATE SET Value = '1'",
                connection);
            command.Parameters.AddWithValue("@key", key);
            command.ExecuteNonQuery();
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

        // Records a full play event for playlist/range history. The uniqueness includes timestamp,
        // channel and song so repeated airings remain distinct while exact feed replays are ignored.
        public bool TryRecordPlayEvent(
            string playId,
            string channel,
            string? trackId,
            string artist,
            string song,
            DateTime timestampUtc,
            ShowWindowRecord? showWindow)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT OR IGNORE INTO PlayEvents (PlayId, Channel, TrackId, Artist, Song, TimestampUtc, ShowWindowId, ProgramId, ShowName, ShowStartUtc, ShowEndUtc) " +
                "VALUES (@playId, @channel, @trackId, @artist, @song, @timestamp, @showWindowId, @programId, @showName, @showStart, @showEnd)",
                connection);

            command.Parameters.AddWithValue("@playId", playId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@trackId", (object?)trackId ?? DBNull.Value);
            command.Parameters.AddWithValue("@artist", artist);
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));
            command.Parameters.AddWithValue("@showWindowId", (object?)showWindow?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("@programId", (object?)showWindow?.ProgramId ?? DBNull.Value);
            command.Parameters.AddWithValue("@showName", (object?)showWindow?.ShowName ?? DBNull.Value);
            command.Parameters.AddWithValue("@showStart", showWindow == null ? DBNull.Value : showWindow.StartUtc.ToString("O"));
            command.Parameters.AddWithValue("@showEnd", showWindow == null ? DBNull.Value : showWindow.EndUtc.ToString("O"));

            return command.ExecuteNonQuery() > 0;
        }

        public void SaveShowWindows(string channel, IEnumerable<ShowInfo> shows)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var cachedUtc = DateTime.UtcNow.ToString("O");

            foreach (var show in shows)
            {
                foreach (var window in show.Windows)
                {
                    using var command = new SQLiteCommand(
                        "INSERT INTO ShowWindows (Channel, ProgramId, ShowName, StartUtc, EndUtc, CachedUtc) " +
                        "VALUES (@channel, @programId, @showName, @start, @end, @cachedUtc) " +
                        "ON CONFLICT(Channel, ProgramId, StartUtc, EndUtc) DO UPDATE SET ShowName = @showName, CachedUtc = @cachedUtc",
                        connection,
                        transaction);

                    command.Parameters.AddWithValue("@channel", channel);
                    command.Parameters.AddWithValue("@programId", show.ProgramId);
                    command.Parameters.AddWithValue("@showName", show.Name);
                    command.Parameters.AddWithValue("@start", window.StartUtc.ToString("O"));
                    command.Parameters.AddWithValue("@end", window.EndUtc.ToString("O"));
                    command.Parameters.AddWithValue("@cachedUtc", cachedUtc);
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        public ShowWindowRecord? GetShowWindowForPlay(string channel, DateTime timestampUtc)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT Id, ProgramId, ShowName, StartUtc, EndUtc FROM ShowWindows " +
                "WHERE Channel = @channel AND StartUtc <= @timestamp AND EndUtc > @timestamp " +
                "ORDER BY StartUtc DESC LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ShowWindowRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                DateTime.Parse(reader.GetString(4)).ToUniversalTime());
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
                "NextRetryUtc = CASE WHEN EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) THEN NULL ELSE NextRetryUtc END, " +
                "RetryAttempts = CASE WHEN EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) THEN 0 ELSE RetryAttempts END",
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
                "WHERE Failures < @maxFailures AND (Resolved = 0 " +
                "OR (NOT EXISTS (SELECT 1 FROM TrackResolutions missing WHERE missing.TrackId = Tracks.TrackId AND missing.AlbumMusicBrainzId IS NULL) " +
                "AND (NOT EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.ReleasePriority = @singles) " +
                "OR NOT EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.ReleasePriority = @albums)))) " +
                "ORDER BY TimestampUtc ASC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@maxFailures", MaxResolutionFailures);
            command.Parameters.AddWithValue("@singles", (int)ReleasePriorityMode.Singles);
            command.Parameters.AddWithValue("@albums", (int)ReleasePriorityMode.Albums);
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
            MarkTrackResolved(trackId, ReleasePriorityMode.Singles, resolution, resolvedUtc);
        }

        public void MarkTrackResolved(string trackId, ReleasePriorityMode releasePriority, AlbumResolution resolution, DateTime? resolvedUtc = null)
        {
            var resolvedAt = resolvedUtc ?? DateTime.UtcNow;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = new SQLiteCommand(
                "INSERT INTO TrackResolutions (TrackId, ReleasePriority, Album, ArtistMusicBrainzId, AlbumMusicBrainzId, ResolvedUtc) " +
                "VALUES (@trackId, @priority, @album, @artistMbid, @albumMbid, @resolvedUtc) " +
                "ON CONFLICT(TrackId, ReleasePriority) DO UPDATE SET Album = @album, ArtistMusicBrainzId = @artistMbid, " +
                "AlbumMusicBrainzId = @albumMbid, ResolvedUtc = @resolvedUtc",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@trackId", trackId);
                command.Parameters.AddWithValue("@priority", (int)releasePriority);
                command.Parameters.AddWithValue("@album", (object?)resolution.Album ?? DBNull.Value);
                command.Parameters.AddWithValue("@artistMbid", (object?)resolution.ArtistMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@albumMbid", (object?)resolution.AlbumMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@resolvedUtc", resolvedAt.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "UPDATE Tracks SET Resolved = 1, Failures = 0, " +
                "Album = CASE WHEN @priority = @singles THEN @album ELSE Album END, " +
                "ArtistMusicBrainzId = CASE WHEN @priority = @singles THEN @artistMbid ELSE ArtistMusicBrainzId END, " +
                "AlbumMusicBrainzId = CASE WHEN @priority = @singles THEN @albumMbid ELSE AlbumMusicBrainzId END, " +
                "ResolvedUtc = @resolvedUtc, " +
                "NextRetryUtc = CASE WHEN EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) THEN @nextRetry ELSE NULL END " +
                "WHERE TrackId = @trackId",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@priority", (int)releasePriority);
                command.Parameters.AddWithValue("@singles", (int)ReleasePriorityMode.Singles);
                command.Parameters.AddWithValue("@album", (object?)resolution.Album ?? DBNull.Value);
                command.Parameters.AddWithValue("@artistMbid", (object?)resolution.ArtistMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@albumMbid", (object?)resolution.AlbumMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@resolvedUtc", resolvedAt.ToString("O"));
                command.Parameters.AddWithValue("@nextRetry", resolvedAt.Add(RetryInterval).ToString("O"));
                command.Parameters.AddWithValue("@trackId", trackId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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
                "WHERE Resolved = 1 AND EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) " +
                "AND RetryAttempts < @maxRetryAttempts " +
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
            using var transaction = connection.BeginTransaction();

            using (var command = new SQLiteCommand(
                "UPDATE Tracks SET RetryAttempts = RetryAttempts + 1, " +
                "NextRetryUtc = @next, ResolvedUtc = @now WHERE TrackId = @trackId",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@next", nowUtc.Add(RetryInterval).ToString("O"));
                command.Parameters.AddWithValue("@now", nowUtc.ToString("O"));
                command.Parameters.AddWithValue("@trackId", trackId);
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "UPDATE TrackResolutions SET ResolvedUtc = @now WHERE TrackId = @trackId",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@now", nowUtc.ToString("O"));
                command.Parameters.AddWithValue("@trackId", trackId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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
        public IReadOnlyList<PresentableTrack> GetPresentableTracks(
            string channel,
            DateTime resolvedSinceUtc,
            int limit,
            IReadOnlyList<ShowWindow>? windows = null,
            bool requireMusicBrainzId = false,
            int minimumPlays = 1,
            ReleasePriorityMode releasePriority = ReleasePriorityMode.Singles)
        {
            return GetPresentableTracks(
                channel,
                resolvedSinceUtc,
                DateTime.UtcNow - PlayRetention,
                limit,
                windows,
                requireMusicBrainzId,
                minimumPlays,
                releasePriority);
        }

        public IReadOnlyList<PresentableTrack> GetPresentableTracks(
            string channel,
            DateTime resolvedSinceUtc,
            DateTime retainedSinceUtc,
            int limit,
            IReadOnlyList<ShowWindow>? windows = null,
            bool requireMusicBrainzId = false,
            int minimumPlays = 1,
            ReleasePriorityMode releasePriority = ReleasePriorityMode.Singles)
        {
            var results = new List<PresentableTrack>();
            var windowFilter = "";
            var mbidFilter = requireMusicBrainzId ? " AND r.AlbumMusicBrainzId IS NOT NULL AND r.AlbumMusicBrainzId <> ''" : "";
            var minimumPlaysFilter = minimumPlays > 1
                ? " AND EXISTS (SELECT 1 FROM Plays p WHERE p.Channel = Tracks.Channel " +
                  "AND lower(trim(p.Song)) = lower(trim(Tracks.Song)) " +
                  "AND instr(lower(Tracks.ArtistsJson), '\"' || lower(trim(p.Artist)) || '\"') > 0 " +
                  "GROUP BY lower(trim(p.Artist)), lower(trim(p.Song)) HAVING COUNT(DISTINCT p.PlayId) >= @minimumPlays)"
                : "";
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
                "SELECT Tracks.TrackId, ArtistsJson, Song, r.Album, r.ArtistMusicBrainzId, r.AlbumMusicBrainzId, TimestampUtc FROM Tracks " +
                "JOIN TrackResolutions r ON r.TrackId = Tracks.TrackId AND r.ReleasePriority = @releasePriority " +
                "WHERE Channel = @channel AND Resolved = 1 AND r.ResolvedUtc >= @resolvedSince AND TimestampUtc >= @retainedSince" + mbidFilter + minimumPlaysFilter + windowFilter + " ORDER BY r.ResolvedUtc DESC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@releasePriority", (int)releasePriority);
            command.Parameters.AddWithValue("@resolvedSince", resolvedSinceUtc.ToString("O"));
            command.Parameters.AddWithValue("@retainedSince", retainedSinceUtc.ToString("O"));
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@minimumPlays", Math.Max(minimumPlays, 1));
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
            using var transaction = connection.BeginTransaction();
            using (var command = new SQLiteCommand("DELETE FROM Plays WHERE TimestampUtc < @cutoff", connection, transaction))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand("DELETE FROM PlayEvents WHERE TimestampUtc < @cutoff", connection, transaction))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand("DELETE FROM ShowWindows WHERE EndUtc < @cutoff", connection, transaction))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand("DELETE FROM TrackResolutions WHERE TrackId IN (SELECT TrackId FROM Tracks WHERE TimestampUtc < @cutoff)", connection, transaction))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand("DELETE FROM Tracks WHERE TimestampUtc < @cutoff", connection, transaction))
            {
                command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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

        // Returns one row per stored artist/play event. Multi-artist plays intentionally have one
        // row per artist; Plex playlist builders should collapse by PlayId/Timestamp if they need
        // one playlist item per aired track.
        public IReadOnlyList<PlayEventRecord> GetPlayEvents(string channel, DateTime sinceUtc, DateTime untilUtc, string? programId = null)
        {
            var results = new List<PlayEventRecord>();
            var showFilter = programId.IsNotNullOrWhiteSpace() ? " AND ProgramId = @programId" : "";

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT PlayEventId, PlayId, Channel, TrackId, Artist, Song, TimestampUtc, ProgramId, ShowName, ShowStartUtc, ShowEndUtc " +
                "FROM PlayEvents WHERE Channel = @channel AND TimestampUtc >= @since AND TimestampUtc < @until" + showFilter +
                " ORDER BY TimestampUtc",
                connection);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@since", sinceUtc.ToString("O"));
            command.Parameters.AddWithValue("@until", untilUtc.ToString("O"));
            if (programId.IsNotNullOrWhiteSpace())
            {
                command.Parameters.AddWithValue("@programId", programId!);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new PlayEventRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)).ToUniversalTime(),
                    reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)).ToUniversalTime()));
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

    public class ShowWindowRecord
    {
        public ShowWindowRecord(long id, string programId, string showName, DateTime startUtc, DateTime endUtc)
        {
            Id = id;
            ProgramId = programId;
            ShowName = showName;
            StartUtc = startUtc;
            EndUtc = endUtc;
        }

        public long Id { get; }
        public string ProgramId { get; }
        public string ShowName { get; }
        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
    }

    public class PlayEventRecord
    {
        public PlayEventRecord(
            long playEventId,
            string playId,
            string channel,
            string? trackId,
            string artist,
            string song,
            DateTime timestampUtc,
            string? programId,
            string? showName,
            DateTime? showStartUtc,
            DateTime? showEndUtc)
        {
            PlayEventId = playEventId;
            PlayId = playId;
            Channel = channel;
            TrackId = trackId;
            Artist = artist;
            Song = song;
            TimestampUtc = timestampUtc;
            ProgramId = programId;
            ShowName = showName;
            ShowStartUtc = showStartUtc;
            ShowEndUtc = showEndUtc;
        }

        public long PlayEventId { get; }
        public string PlayId { get; }
        public string Channel { get; }
        public string? TrackId { get; }
        public string Artist { get; }
        public string Song { get; }
        public DateTime TimestampUtc { get; }
        public string? ProgramId { get; }
        public string? ShowName { get; }
        public DateTime? ShowStartUtc { get; }
        public DateTime? ShowEndUtc { get; }
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
