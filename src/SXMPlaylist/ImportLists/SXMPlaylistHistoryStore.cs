using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
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
        public static readonly TimeSpan PlayRetention = TimeSpan.FromDays(365);
        public static readonly TimeSpan CaptureInterval = TimeSpan.FromHours(1);
        public static readonly TimeSpan PresentationWindow = TimeSpan.FromHours(25);

        // Transient failures (MB 503/429 that survived in-resolver retries) are re-attempted
        // on a long cadence. Permanent failures (404, no MB data, etc.) are never retried.
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
                "SongKey TEXT NOT NULL DEFAULT '', " +
                "TimestampUtc TEXT NOT NULL, " +
                "PRIMARY KEY (PlayId, Artist))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            // Normalized song key (lowercased + trimmed) so the minimumPlays EXISTS filter can use an
            // index instead of lower()/trim() over every Plays row per candidate track. Backfilled
            // for rows written before the column existed; new rows always set it.
            if (EnsureColumn(connection, "Plays", "SongKey", "TEXT NOT NULL DEFAULT ''"))
            {
                using (var backfill = new SQLiteCommand(
                    "UPDATE Plays SET SongKey = lower(trim(Song)) WHERE SongKey = '' OR SongKey IS NULL",
                    connection))
                {
                    backfill.ExecuteNonQuery();
                }
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Plays_Channel_TimestampUtc ON Plays (Channel, TimestampUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Plays_Channel_SongKey ON Plays (Channel, SongKey)",
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
                "SongKey TEXT NOT NULL DEFAULT '', " +
                "DeezerUrl TEXT, " +
                "AppleMusicUrl TEXT, " +
                "TimestampUtc TEXT NOT NULL, " +
                "Resolved INTEGER NOT NULL DEFAULT 0, " +
                "Album TEXT, " +
                "ArtistMusicBrainzId TEXT, " +
                "AlbumMusicBrainzId TEXT, " +
                "RecordingMusicBrainzId TEXT, " +
                "TrackMusicBrainzId TEXT, " +
                "Isrc TEXT, " +
                "ResolutionMethod TEXT, " +
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
            EnsureColumn(connection, "Tracks", "RecordingMusicBrainzId", "TEXT");
            EnsureColumn(connection, "Tracks", "TrackMusicBrainzId", "TEXT");
            EnsureColumn(connection, "Tracks", "Isrc", "TEXT");
            EnsureColumn(connection, "Tracks", "ResolutionMethod", "TEXT");

            // The pre-retry-sweep schema carried a Failures counter that is never read anywhere
            // (the unified retry system tracks RetryAttempts/NextRetryUtc instead; RecordTrackFailure
            // was dead code). Drop the dead column — idempotent, no-op on DBs that never had it.
            DropColumnIfExists(connection, "Tracks", "Failures");

            // Normalized song key for the minimumPlays EXISTS filter (indexed on Plays). Backfilled
            // for rows written before the column existed; new rows always set it via UpsertTrack.
            if (EnsureColumn(connection, "Tracks", "SongKey", "TEXT NOT NULL DEFAULT ''"))
            {
                using (var backfill = new SQLiteCommand(
                    "UPDATE Tracks SET SongKey = lower(trim(Song)) WHERE SongKey = '' OR SongKey IS NULL",
                    connection))
                {
                    backfill.ExecuteNonQuery();
                }
            }

            // Only when this is a genuine first migration (a column was just added): historical
            // no-MBID rows have NextRetryUtc = NULL, which GetDueTracks would treat as immediately
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

            // Serve the two hottest worker queries without full-table scans + temp sorts:
            // GetDueTracks (unified queue: new captures + transient retries)
            // sweep, ORDER BY NextRetryUtc). Partial indexes keep write cost low.
            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Tracks_Due ON Tracks (TimestampUtc) WHERE Resolved = 0",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_Tracks_Retries ON Tracks (NextRetryUtc) WHERE Resolved = 1",
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

            // One row per import list that opted into a companion Plex playlist. Persists the Plex
            // playlist ratingKey so we only ever touch playlists we created (find-by-title is the
            // fallback for first-run), the last sync time so the worker can throttle refreshes, a
            // JSON cache of matched (artist||title) -> Plex track ratingKeys so repeat syncs don't
            // re-search the Plex library for tracks already matched, and the per-Plex-Home-user
            // playlist ratingKeys (username -> ratingKey) for the fan-out copies.
            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS PlexPlaylistState (" +
                "ListId INTEGER PRIMARY KEY, " +
                "PlaylistTitle TEXT NOT NULL, " +
                "PlaylistRatingKey TEXT NOT NULL, " +
                "LastSyncUtc TEXT NOT NULL, " +
                "TrackCacheJson TEXT NOT NULL DEFAULT '{}', " +
                "UserPlaylistKeysJson TEXT NOT NULL DEFAULT '{}')",
                connection))
            {
                command.ExecuteNonQuery();
            }

            // Upgrade databases created before the track cache / user keys columns existed (idempotent).
            EnsureColumn(connection, "PlexPlaylistState", "TrackCacheJson", "TEXT NOT NULL DEFAULT '{}'");
            EnsureColumn(connection, "PlexPlaylistState", "UserPlaylistKeysJson", "TEXT NOT NULL DEFAULT '{}'");

            using (var command = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS PlexPlaylistTrackMatches (" +
                "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "ListId INTEGER NOT NULL, " +
                "SyncUtc TEXT NOT NULL, " +
                "PlayId TEXT NOT NULL, " +
                "SxmTrackId TEXT, " +
                "Channel TEXT NOT NULL, " +
                "Artist TEXT NOT NULL, " +
                "Song TEXT NOT NULL, " +
                "TimestampUtc TEXT NOT NULL, " +
                "RecordingMusicBrainzId TEXT, " +
                "Isrc TEXT, " +
                "PlexRatingKey TEXT, " +
                "PlexArtist TEXT, " +
                "PlexTitle TEXT, " +
                "PlexAlbum TEXT, " +
                "PlexGuid TEXT, " +
                "MatchMethod TEXT NOT NULL, " +
                "Confidence TEXT NOT NULL, " +
                "MbidMatchStatus TEXT, " +
                "UNIQUE(ListId, SyncUtc, PlayId))",
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (var command = new SQLiteCommand(
                "CREATE INDEX IF NOT EXISTS IX_PlexPlaylistTrackMatches_List_Time ON PlexPlaylistTrackMatches (ListId, SyncUtc)",
                connection))
            {
                command.ExecuteNonQuery();
            }

            // SQLite cannot add a UNIQUE constraint to an existing table (no ALTER TABLE ADD
            // CONSTRAINT), so databases created before the constraint was added to the schema need
            // a table recreate to get it. The constraint only guards against accidental
            // within-sync duplicate audit rows (the writer already dedupes by PlayId per sync), so
            // a failed recreate must never block plugin startup — log and continue without it.
            if (!GetSchemaFlag(connection, "PlexPlaylistTrackMatchesUniqueMigration"))
            {
                try
                {
                    using (var recreate = new SQLiteCommand(
                        "CREATE TABLE PlexPlaylistTrackMatches_New (" +
                        "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                        "ListId INTEGER NOT NULL, " +
                        "SyncUtc TEXT NOT NULL, " +
                        "PlayId TEXT NOT NULL, " +
                        "SxmTrackId TEXT, " +
                        "Channel TEXT NOT NULL, " +
                        "Artist TEXT NOT NULL, " +
                        "Song TEXT NOT NULL, " +
                        "TimestampUtc TEXT NOT NULL, " +
                        "RecordingMusicBrainzId TEXT, " +
                        "Isrc TEXT, " +
                        "PlexRatingKey TEXT, " +
                        "PlexArtist TEXT, " +
                        "PlexTitle TEXT, " +
                        "PlexAlbum TEXT, " +
                        "PlexGuid TEXT, " +
                        "MatchMethod TEXT NOT NULL, " +
                        "Confidence TEXT NOT NULL, " +
                        "UNIQUE(ListId, SyncUtc, PlayId)); " +
                        // INSERT OR IGNORE so pre-existing duplicate rows (written before the
                        // constraint existed) collapse during the copy instead of failing it.
                        "INSERT OR IGNORE INTO PlexPlaylistTrackMatches_New (Id, ListId, SyncUtc, PlayId, SxmTrackId, Channel, Artist, Song, TimestampUtc, RecordingMusicBrainzId, Isrc, PlexRatingKey, PlexArtist, PlexTitle, PlexAlbum, PlexGuid, MatchMethod, Confidence) " +
                        "SELECT Id, ListId, SyncUtc, PlayId, SxmTrackId, Channel, Artist, Song, TimestampUtc, RecordingMusicBrainzId, Isrc, PlexRatingKey, PlexArtist, PlexTitle, PlexAlbum, PlexGuid, MatchMethod, Confidence FROM PlexPlaylistTrackMatches; " +
                        "DROP TABLE PlexPlaylistTrackMatches; " +
                        "ALTER TABLE PlexPlaylistTrackMatches_New RENAME TO PlexPlaylistTrackMatches",
                        connection))
                    {
                        recreate.ExecuteNonQuery();
                    }

                    // Recreate the supporting index that DROP TABLE removed.
                    using (var reindex = new SQLiteCommand(
                        "CREATE INDEX IF NOT EXISTS IX_PlexPlaylistTrackMatches_List_Time ON PlexPlaylistTrackMatches (ListId, SyncUtc)",
                        connection))
                    {
                        reindex.ExecuteNonQuery();
                    }

                    SetSchemaFlag(connection, "PlexPlaylistTrackMatchesUniqueMigration");
                }
                catch (SQLiteException ex)
                {
                    // Defensive constraint only — never take the plugin down over it.
                    LogManager.GetCurrentClassLogger().Warn(ex, "Could not recreate PlexPlaylistTrackMatches with UNIQUE constraint; continuing without it");
                }
            }

            // Audit rows now record whether the SXM recording MBID matched the Plex track's mbid:// GUID.
            EnsureColumn(connection, "PlexPlaylistTrackMatches", "MbidMatchStatus", "TEXT");
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
            try
            {
                alter.ExecuteNonQuery();
                return true;
            }
            catch (SQLiteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Two import lists construct their store concurrently on first start after an
                // upgrade; both can pass the PRAGMA check, and the loser's ALTER fails with
                // "duplicate column name". The column exists either way — treat as already-migrated.
                return false;
            }
        }

        // Removes a column that the current schema no longer uses. Idempotent: skips when the
        // column is absent, so it is safe to call on every Initialize() alongside EnsureColumn.
        // Mirrors EnsureColumn's PRAGMA guard (and its concurrent-construct race tolerance).
        private static void DropColumnIfExists(SQLiteConnection connection, string table, string column)
        {
            using var check = new SQLiteCommand($"PRAGMA table_info({table})", connection);
            var present = false;
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }
            }

            if (!present)
            {
                return;
            }

            using var drop = new SQLiteCommand($"ALTER TABLE {table} DROP COLUMN {column}", connection);
            try
            {
                drop.ExecuteNonQuery();
            }
            catch (SQLiteException ex) when (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                // Two import lists construct their store concurrently; the loser's DROP races a
                // winner that already removed the column. Column is gone either way.
            }
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
                "INSERT OR IGNORE INTO Plays (PlayId, Channel, Artist, Song, SongKey, TimestampUtc) VALUES (@playId, @channel, @artist, @song, @songKey, @timestamp)",
                connection);

            command.Parameters.AddWithValue("@playId", playId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artist", artist);
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@songKey", SongKeyOf(song));
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

        public void SaveShowWindows(string channel, IEnumerable<ShowInfo> shows, DateTime? cachedUtc = null)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var cachedAt = (cachedUtc ?? DateTime.UtcNow).ToString("O");

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
                    command.Parameters.AddWithValue("@cachedUtc", cachedAt);
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        public DateTime? GetShowWindowsCacheAge(string channel)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand("SELECT MAX(CachedUtc) FROM ShowWindows WHERE Channel = @channel", connection);
            command.Parameters.AddWithValue("@channel", channel);

            var result = command.ExecuteScalar();
            return result == null || result is DBNull ? (DateTime?)null : DateTime.Parse((string)result).ToUniversalTime();
        }

        // Returns the persisted show windows for a channel + program (saved by the worker's EPG
        // refresh). Used as a fallback when the live EPG fetch fails, so show-filtered lists keep
        // presenting from the last known schedule instead of returning nothing.
        public IReadOnlyList<ShowWindowRecord> GetCachedShowWindows(string channel, string programId)
        {
            var results = new List<ShowWindowRecord>();
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT Id, ProgramId, ShowName, StartUtc, EndUtc FROM ShowWindows " +
                "WHERE Channel = @channel AND ProgramId = @programId ORDER BY StartUtc ASC",
                connection);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@programId", programId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ShowWindowRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                    DateTime.Parse(reader.GetString(4)).ToUniversalTime()));
            }

            return results;
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
                "INSERT INTO Tracks (TrackId, Channel, ArtistsJson, Song, SongKey, DeezerUrl, AppleMusicUrl, TimestampUtc) " +
                "VALUES (@trackId, @channel, @artists, @song, @songKey, @deezerUrl, @appleMusicUrl, @timestamp) " +
                "ON CONFLICT(TrackId) DO UPDATE SET Channel = @channel, ArtistsJson = @artists, Song = @song, SongKey = @songKey, " +
                "DeezerUrl = @deezerUrl, AppleMusicUrl = @appleMusicUrl, TimestampUtc = @timestamp, " +
                "NextRetryUtc = NULL, " +
                "RetryAttempts = CASE WHEN EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) THEN 0 ELSE RetryAttempts END",
                connection);

            command.Parameters.AddWithValue("@trackId", trackId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artists", JsonConvert.SerializeObject(artists));
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@songKey", SongKeyOf(song));
            command.Parameters.AddWithValue("@deezerUrl", (object?)deezerUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@appleMusicUrl", (object?)appleMusicUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            command.ExecuteNonQuery();
        }

        // Batch capture: records plays, play events, and track upserts for a whole channel capture
        // in a single connection + transaction, instead of one connection + autocommit per play and
        // per artist. Returns the number of tracks that were newly captured (not previously seen).
        // Show-window attribution is resolved in memory from one window read per channel, so the
        // per-play GetShowWindowForPlay round-trip is eliminated too.
        public int RecordCapture(IReadOnlyList<CapturePlay> plays)
        {
            if (plays.Count == 0)
            {
                return 0;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var captured = 0;
            var windowsByChannel = new Dictionary<string, IReadOnlyList<ShowWindowRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (var play in plays)
            {
                if (!windowsByChannel.TryGetValue(play.Channel, out var windows))
                {
                    windows = LoadShowWindows(connection, play.Channel);
                    windowsByChannel[play.Channel] = windows;
                }

                var showWindow = MatchShowWindow(windows, play.TimestampUtc);
                var isNew = false;

                foreach (var artist in play.Artists)
                {
                    if (artist.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    isNew |= TryRecordPlay(connection, transaction, play.PlayId, play.Channel, artist, play.Song, play.TimestampUtc);
                    isNew |= TryRecordPlayEvent(connection, transaction, play.PlayId, play.Channel, play.TrackId, artist, play.Song, play.TimestampUtc, showWindow);
                }

                if (isNew)
                {
                    UpsertTrack(connection, transaction, play.TrackId, play.Channel, play.Artists, play.Song, play.DeezerUrl, play.AppleMusicUrl, play.TimestampUtc);
                    captured++;
                }
            }

            transaction.Commit();
            return captured;
        }

        private static ShowWindowRecord? MatchShowWindow(IReadOnlyList<ShowWindowRecord> windows, DateTime timestampUtc)
        {
            ShowWindowRecord? best = null;
            foreach (var window in windows)
            {
                if (window.StartUtc <= timestampUtc && window.EndUtc > timestampUtc && (best == null || window.StartUtc > best.StartUtc))
                {
                    best = window;
                }
            }

            return best;
        }

        private static IReadOnlyList<ShowWindowRecord> LoadShowWindows(SQLiteConnection connection, string channel)
        {
            var windows = new List<ShowWindowRecord>();
            using var command = new SQLiteCommand(
                "SELECT Id, ProgramId, ShowName, StartUtc, EndUtc FROM ShowWindows WHERE Channel = @channel ORDER BY StartUtc ASC",
                connection);

            command.Parameters.AddWithValue("@channel", channel);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                windows.Add(new ShowWindowRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                    DateTime.Parse(reader.GetString(4)).ToUniversalTime()));
            }

            return windows;
        }

        private static bool TryRecordPlay(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string playId,
            string channel,
            string artist,
            string song,
            DateTime timestampUtc)
        {
            using var command = new SQLiteCommand(
                "INSERT OR IGNORE INTO Plays (PlayId, Channel, Artist, Song, SongKey, TimestampUtc) VALUES (@playId, @channel, @artist, @song, @songKey, @timestamp)",
                connection,
                transaction);

            command.Parameters.AddWithValue("@playId", playId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artist", artist);
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@songKey", SongKeyOf(song));
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            return command.ExecuteNonQuery() > 0;
        }

        private static bool TryRecordPlayEvent(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string playId,
            string channel,
            string? trackId,
            string artist,
            string song,
            DateTime timestampUtc,
            ShowWindowRecord? showWindow)
        {
            using var command = new SQLiteCommand(
                "INSERT OR IGNORE INTO PlayEvents (PlayId, Channel, TrackId, Artist, Song, TimestampUtc, ShowWindowId, ProgramId, ShowName, ShowStartUtc, ShowEndUtc) " +
                "VALUES (@playId, @channel, @trackId, @artist, @song, @timestamp, @showWindowId, @programId, @showName, @showStart, @showEnd)",
                connection,
                transaction);

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

        private static void UpsertTrack(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string trackId,
            string channel,
            IReadOnlyList<string> artists,
            string song,
            string? deezerUrl,
            string? appleMusicUrl,
            DateTime timestampUtc)
        {
            using var command = new SQLiteCommand(
                "INSERT INTO Tracks (TrackId, Channel, ArtistsJson, Song, SongKey, DeezerUrl, AppleMusicUrl, TimestampUtc) " +
                "VALUES (@trackId, @channel, @artists, @song, @songKey, @deezerUrl, @appleMusicUrl, @timestamp) " +
                "ON CONFLICT(TrackId) DO UPDATE SET Channel = @channel, ArtistsJson = @artists, Song = @song, SongKey = @songKey, " +
                "DeezerUrl = @deezerUrl, AppleMusicUrl = @appleMusicUrl, TimestampUtc = @timestamp, " +
                "NextRetryUtc = NULL, " +
                "RetryAttempts = CASE WHEN EXISTS (SELECT 1 FROM TrackResolutions r WHERE r.TrackId = Tracks.TrackId AND r.AlbumMusicBrainzId IS NULL) THEN 0 ELSE RetryAttempts END",
                connection,
                transaction);

            command.Parameters.AddWithValue("@trackId", trackId);
            command.Parameters.AddWithValue("@channel", channel);
            command.Parameters.AddWithValue("@artists", JsonConvert.SerializeObject(artists));
            command.Parameters.AddWithValue("@song", song);
            command.Parameters.AddWithValue("@songKey", SongKeyOf(song));
            command.Parameters.AddWithValue("@deezerUrl", (object?)deezerUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@appleMusicUrl", (object?)appleMusicUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@timestamp", timestampUtc.ToString("O"));

            command.ExecuteNonQuery();
        }

        // Normalized song key matching the SQL lower(trim(Song)) comparisons, so C#-side writes and
        // the minimumPlays EXISTS filter agree on the key format.
        private static string SongKeyOf(string song)
        {
            return (song ?? "").Trim().ToLowerInvariant();
        }

        // Tracks that still need resolution and haven't exhausted their retries, oldest first.
        public IReadOnlyList<PendingTrack> GetDueTracks(int limit, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var results = new List<PendingTrack>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT TrackId, Channel, ArtistsJson, Song, DeezerUrl, AppleMusicUrl FROM Tracks " +
                "WHERE Resolved = 0 " +
                "AND RetryAttempts < @maxRetryAttempts " +
                "AND (NextRetryUtc IS NULL OR NextRetryUtc <= @now) " +
                "ORDER BY NextRetryUtc ASC NULLS FIRST, TimestampUtc ASC LIMIT @limit",
                connection);

            command.Parameters.AddWithValue("@maxRetryAttempts", MaxRetryAttempts);
            command.Parameters.AddWithValue("@now", now.ToString("O"));
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

        // Tracks with an album MBID but no recording or track MBID — candidates for Lidarr backfill.
        public IReadOnlyList<(string TrackId, string AlbumMbid, string Song, string Channel)> GetTracksWithoutRecordingMbid()
        {
            var results = new List<(string, string, string, string)>();

            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT TrackId, AlbumMusicBrainzId, Song, Channel FROM Tracks " +
                "WHERE AlbumMusicBrainzId IS NOT NULL AND AlbumMusicBrainzId <> '' " +
                "AND ((RecordingMusicBrainzId IS NULL OR RecordingMusicBrainzId = '') " +
                "OR (TrackMusicBrainzId IS NULL OR TrackMusicBrainzId = ''))",
                connection);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }

            return results;
        }

        public void UpdateRecordingMusicBrainzId(string trackId, string recordingMusicBrainzId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET RecordingMusicBrainzId = @recordingMbid WHERE TrackId = @trackId",
                connection);

            command.Parameters.AddWithValue("@recordingMbid", recordingMusicBrainzId);
            command.Parameters.AddWithValue("@trackId", trackId);
            command.ExecuteNonQuery();
        }

        public void UpdateTrackMusicBrainzId(string trackId, string trackMusicBrainzId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET TrackMusicBrainzId = @trackMbid WHERE TrackId = @trackId",
                connection);

            command.Parameters.AddWithValue("@trackMbid", trackMusicBrainzId);
            command.Parameters.AddWithValue("@trackId", trackId);
            command.ExecuteNonQuery();
        }

        public void MarkTrackResolved(string trackId, AlbumResolution resolution, DateTime? resolvedUtc = null)
        {
            MarkTrackResolved(trackId, ReleasePriorityMode.Singles, resolution, resolvedUtc);
        }

        public void MarkTrackResolved(
            string trackId,
            ReleasePriorityMode releasePriority,
            AlbumResolution resolution,
            DateTime? resolvedUtc = null)
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
                "UPDATE Tracks SET Resolved = 1, " +
                "Album = CASE WHEN @priority = @singles THEN @album ELSE Album END, " +
                "ArtistMusicBrainzId = CASE WHEN @priority = @singles THEN @artistMbid ELSE ArtistMusicBrainzId END, " +
                "AlbumMusicBrainzId = CASE WHEN @priority = @singles THEN @albumMbid ELSE AlbumMusicBrainzId END, " +
                "RecordingMusicBrainzId = COALESCE(@recordingMbid, RecordingMusicBrainzId), " +
                "TrackMusicBrainzId = COALESCE(@trackMbid, TrackMusicBrainzId), " +
                "Isrc = COALESCE(@isrc, Isrc), " +
                "ResolutionMethod = COALESCE(@resolutionMethod, ResolutionMethod), " +
                "ResolvedUtc = @resolvedUtc, " +
                "NextRetryUtc = NULL " +
                "WHERE TrackId = @trackId",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@priority", (int)releasePriority);
                command.Parameters.AddWithValue("@singles", (int)ReleasePriorityMode.Singles);
                command.Parameters.AddWithValue("@album", (object?)resolution.Album ?? DBNull.Value);
                command.Parameters.AddWithValue("@artistMbid", (object?)resolution.ArtistMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@albumMbid", (object?)resolution.AlbumMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@recordingMbid", (object?)resolution.RecordingMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@trackMbid", (object?)resolution.TrackMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@isrc", (object?)resolution.Isrc ?? DBNull.Value);
                command.Parameters.AddWithValue("@resolutionMethod", (object?)resolution.ResolutionMethod ?? DBNull.Value);
                command.Parameters.AddWithValue("@resolvedUtc", resolvedAt.ToString("O"));
                command.Parameters.AddWithValue("@trackId", trackId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // track in the Tracks row, then writes the same renewal into TrackResolutions so either
        // table's view of the state stays consistent. Give-up is implicit: GetDueTracks stops
        // selecting once RetryAttempts hits MaxRetryAttempts.
        public void RecordTransientFailure(string trackId, DateTime nowUtc)
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

        // A permanent failure (404, no MB data, artist mismatch, etc.) — the track is marked
        // resolved with no MBID and never retried. The album title is set to whatever we have
        // from the provider (Deezer/Apple) or "Unknown" if nothing was available.
        public void RecordPermanentFailure(string trackId, string? albumTitle)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "UPDATE Tracks SET Resolved = 1, Album = @album, " +
                "NextRetryUtc = NULL, ResolvedUtc = @now WHERE TrackId = @trackId",
                connection);

            command.Parameters.AddWithValue("@album", string.IsNullOrEmpty(albumTitle) ? "Unknown" : albumTitle);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
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
                  "AND p.SongKey = Tracks.SongKey " +
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
                "SELECT Tracks.TrackId, ArtistsJson, Song, r.Album, r.ArtistMusicBrainzId, r.AlbumMusicBrainzId, TimestampUtc, alt.AlbumMusicBrainzId FROM Tracks " +
                "JOIN TrackResolutions r ON r.TrackId = Tracks.TrackId AND r.ReleasePriority = @releasePriority " +
                "LEFT JOIN TrackResolutions alt ON alt.TrackId = Tracks.TrackId AND alt.ReleasePriority <> @releasePriority " +
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
                    timestampUtc,
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
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

        public PlexPlaylistStateRecord? GetPlexPlaylistState(long listId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT ListId, PlaylistTitle, PlaylistRatingKey, LastSyncUtc, TrackCacheJson, UserPlaylistKeysJson FROM PlexPlaylistState WHERE ListId = @listId",
                connection);
            command.Parameters.AddWithValue("@listId", listId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PlexPlaylistStateRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                DeserializeTrackCache(reader.IsDBNull(4) ? null : reader.GetString(4)),
                DeserializeStringMap(reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        // Enumerates every persisted companion-playlist state row. Used by the worker to find lists
        // whose "Companion Plex Playlist" option has since been disabled so their playlists can be
        // deleted rather than left orphaned.
        public List<PlexPlaylistStateRecord> GetAllPlexPlaylistState()
        {
            var result = new List<PlexPlaylistStateRecord>();
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "SELECT ListId, PlaylistTitle, PlaylistRatingKey, LastSyncUtc, TrackCacheJson, UserPlaylistKeysJson FROM PlexPlaylistState",
                connection);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PlexPlaylistStateRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                    DeserializeTrackCache(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    DeserializeStringMap(reader.IsDBNull(5) ? null : reader.GetString(5))));
            }

            return result;
        }

        public void UpsertPlexPlaylistState(
            long listId,
            string playlistTitle,
            string playlistRatingKey,
            DateTime lastSyncUtc,
            IReadOnlyDictionary<string, PlexTrackCacheRecord>? trackCache = null,
            IReadOnlyDictionary<string, string>? userPlaylistKeys = null)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(
                "INSERT INTO PlexPlaylistState (ListId, PlaylistTitle, PlaylistRatingKey, LastSyncUtc, TrackCacheJson, UserPlaylistKeysJson) VALUES (@listId, @title, @ratingKey, @utc, @cache, @userKeys) " +
                "ON CONFLICT(ListId) DO UPDATE SET PlaylistTitle = @title, PlaylistRatingKey = @ratingKey, LastSyncUtc = @utc, TrackCacheJson = @cache, UserPlaylistKeysJson = @userKeys",
                connection);
            command.Parameters.AddWithValue("@listId", listId);
            command.Parameters.AddWithValue("@title", playlistTitle);
            command.Parameters.AddWithValue("@ratingKey", playlistRatingKey);
            command.Parameters.AddWithValue("@utc", lastSyncUtc.ToString("O"));
            command.Parameters.AddWithValue("@cache", SerializeTrackCache(trackCache));
            command.Parameters.AddWithValue("@userKeys", SerializeStringMap(userPlaylistKeys));
            command.ExecuteNonQuery();
        }

        private static string SerializeTrackCache(IReadOnlyDictionary<string, PlexTrackCacheRecord>? trackCache)
        {
            if (trackCache == null || trackCache.Count == 0)
            {
                return "{}";
            }

            return JsonConvert.SerializeObject(trackCache);
        }

        private static Dictionary<string, PlexTrackCacheRecord> DeserializeTrackCache(string? json)
        {
            if (json.IsNullOrWhiteSpace())
            {
                return new Dictionary<string, PlexTrackCacheRecord>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var parsed = JObject.Parse(json!);
                var result = new Dictionary<string, PlexTrackCacheRecord>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in parsed.Properties())
                {
                    if (property.Value.Type == JTokenType.String)
                    {
                        var ratingKey = property.Value.Value<string>();
                        if (ratingKey.IsNotNullOrWhiteSpace())
                        {
                            result[property.Name] = new PlexTrackCacheRecord(ratingKey!, null, null, null, null, "cache", "unknown");
                        }
                    }
                    else if (property.Value.Type == JTokenType.Object)
                    {
                        var record = property.Value.ToObject<PlexTrackCacheRecord>();
                        if (record?.RatingKey.IsNotNullOrWhiteSpace() == true)
                        {
                            result[property.Name] = record;
                        }
                    }
                }

                return result;
            }
            catch (Exception)
            {
                return new Dictionary<string, PlexTrackCacheRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string SerializeStringMap(IReadOnlyDictionary<string, string>? values)
        {
            if (values == null || values.Count == 0)
            {
                return "{}";
            }

            return JsonConvert.SerializeObject(values);
        }

        private static Dictionary<string, string> DeserializeStringMap(string? json)
        {
            if (json.IsNullOrWhiteSpace())
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json!)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void DeletePlexPlaylistState(long listId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand("DELETE FROM PlexPlaylistState WHERE ListId = @listId", connection);
            command.Parameters.AddWithValue("@listId", listId);
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

            using (var command = new SQLiteCommand("DELETE FROM PlexPlaylistTrackMatches WHERE SyncUtc < @cutoff", connection, transaction))
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
                "SELECT p.PlayEventId, p.PlayId, p.Channel, p.TrackId, p.Artist, p.Song, p.TimestampUtc, p.ProgramId, p.ShowName, p.ShowStartUtc, p.ShowEndUtc, t.RecordingMusicBrainzId, t.TrackMusicBrainzId, t.Isrc, t.Album " +
                "FROM PlayEvents p LEFT JOIN Tracks t ON t.TrackId = p.TrackId WHERE p.Channel = @channel AND p.TimestampUtc >= @since AND p.TimestampUtc < @until" + showFilter +
                " ORDER BY p.TimestampUtc",
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
                    reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)).ToUniversalTime(),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }

            return results;
        }

        public void RecordPlexPlaylistTrackMatches(long listId, DateTime syncUtc, IReadOnlyList<PlexPlaylistTrackMatchRecord> matches)
        {
            if (matches == null || matches.Count == 0)
            {
                return;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var match in matches)
            {
                using var command = new SQLiteCommand(
                    "INSERT OR IGNORE INTO PlexPlaylistTrackMatches (ListId, SyncUtc, PlayId, SxmTrackId, Channel, Artist, Song, TimestampUtc, RecordingMusicBrainzId, Isrc, PlexRatingKey, PlexArtist, PlexTitle, PlexAlbum, PlexGuid, MatchMethod, Confidence, MbidMatchStatus) " +
                    "VALUES (@listId, @syncUtc, @playId, @sxmTrackId, @channel, @artist, @song, @timestampUtc, @recordingMbid, @isrc, @plexRatingKey, @plexArtist, @plexTitle, @plexAlbum, @plexGuid, @matchMethod, @confidence, @mbidMatchStatus)",
                    connection,
                    transaction);

                command.Parameters.AddWithValue("@listId", listId);
                command.Parameters.AddWithValue("@syncUtc", syncUtc.ToString("O"));
                command.Parameters.AddWithValue("@playId", match.PlayId);
                command.Parameters.AddWithValue("@sxmTrackId", (object?)match.SxmTrackId ?? DBNull.Value);
                command.Parameters.AddWithValue("@channel", match.Channel);
                command.Parameters.AddWithValue("@artist", match.Artist);
                command.Parameters.AddWithValue("@song", match.Song);
                command.Parameters.AddWithValue("@timestampUtc", match.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("@recordingMbid", (object?)match.RecordingMusicBrainzId ?? DBNull.Value);
                command.Parameters.AddWithValue("@isrc", (object?)match.Isrc ?? DBNull.Value);
                command.Parameters.AddWithValue("@plexRatingKey", (object?)match.PlexRatingKey ?? DBNull.Value);
                command.Parameters.AddWithValue("@plexArtist", (object?)match.PlexArtist ?? DBNull.Value);
                command.Parameters.AddWithValue("@plexTitle", (object?)match.PlexTitle ?? DBNull.Value);
                command.Parameters.AddWithValue("@plexAlbum", (object?)match.PlexAlbum ?? DBNull.Value);
                command.Parameters.AddWithValue("@plexGuid", (object?)match.PlexGuid ?? DBNull.Value);
                command.Parameters.AddWithValue("@matchMethod", match.MatchMethod);
                command.Parameters.AddWithValue("@confidence", match.Confidence);
                command.Parameters.AddWithValue("@mbidMatchStatus", (object?)match.MbidMatchStatus ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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

            // The plugin shares this SQLite file across threads (worker capture/write, import-list
            // reads, channel-directory refresh). SQLite's default busy_timeout is 0, so any second
            // concurrent writer fails immediately with SQLITE_BUSY. WAL allows one writer + N
            // readers; give writers up to 30s to get the lock instead of throwing.
            using var busy = new SQLiteCommand("PRAGMA busy_timeout=30000", connection);
            busy.ExecuteNonQuery();

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

    // One parsed play from a channel capture, handed to RecordCapture for a single-transaction
    // batch write. PlayId is the xmplaylist play id; TrackId is the underlying track id.
    public class CapturePlay
    {
        public CapturePlay(
            string playId,
            string channel,
            string trackId,
            IReadOnlyList<string> artists,
            string song,
            DateTime timestampUtc,
            string? deezerUrl = null,
            string? appleMusicUrl = null)
        {
            PlayId = playId;
            Channel = channel;
            TrackId = trackId;
            Artists = artists;
            Song = song;
            TimestampUtc = timestampUtc;
            DeezerUrl = deezerUrl;
            AppleMusicUrl = appleMusicUrl;
        }

        public string PlayId { get; }
        public string Channel { get; }
        public string TrackId { get; }
        public IReadOnlyList<string> Artists { get; }
        public string Song { get; }
        public DateTime TimestampUtc { get; }
        public string? DeezerUrl { get; }
        public string? AppleMusicUrl { get; }
    }

    public class PresentableTrack
    {
        public PresentableTrack(
            string trackId,
            IReadOnlyList<string> artists,
            string song,
            string? album,
            string? artistMusicBrainzId,
            string? albumMusicBrainzId,
            DateTime timestampUtc,
            string? alternateAlbumMusicBrainzId = null)
        {
            TrackId = trackId;
            Artists = artists;
            Song = song;
            Album = album;
            ArtistMusicBrainzId = artistMusicBrainzId;
            AlbumMusicBrainzId = albumMusicBrainzId;
            TimestampUtc = timestampUtc;
            AlternateAlbumMusicBrainzId = alternateAlbumMusicBrainzId;
        }

        public string TrackId { get; }
        public IReadOnlyList<string> Artists { get; }
        public string Song { get; }
        public string? Album { get; }
        public string? ArtistMusicBrainzId { get; }
        public string? AlbumMusicBrainzId { get; }
        public DateTime TimestampUtc { get; }
        public string? AlternateAlbumMusicBrainzId { get; }
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
            DateTime? showEndUtc,
            string? recordingMusicBrainzId = null,
            string? trackMusicBrainzId = null,
            string? isrc = null,
            string? album = null)
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
            RecordingMusicBrainzId = recordingMusicBrainzId;
            TrackMusicBrainzId = trackMusicBrainzId;
            Isrc = isrc;
            Album = album;
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
        public string? RecordingMusicBrainzId { get; }
        public string? TrackMusicBrainzId { get; }
        public string? Isrc { get; }
        public string? Album { get; }
    }

    public class PlexPlaylistTrackMatchRecord
    {
        public PlexPlaylistTrackMatchRecord(
            string playId,
            string? sxmTrackId,
            string channel,
            string artist,
            string song,
            DateTime timestampUtc,
            string? recordingMusicBrainzId,
            string? isrc,
            string? plexRatingKey,
            string? plexArtist,
            string? plexTitle,
            string? plexAlbum,
            string? plexGuid,
            string matchMethod,
            string confidence,
            string? mbidMatchStatus = null)
        {
            PlayId = playId;
            SxmTrackId = sxmTrackId;
            Channel = channel;
            Artist = artist;
            Song = song;
            TimestampUtc = timestampUtc;
            RecordingMusicBrainzId = recordingMusicBrainzId;
            Isrc = isrc;
            PlexRatingKey = plexRatingKey;
            PlexArtist = plexArtist;
            PlexTitle = plexTitle;
            PlexAlbum = plexAlbum;
            PlexGuid = plexGuid;
            MatchMethod = matchMethod;
            Confidence = confidence;
            MbidMatchStatus = mbidMatchStatus ?? "unavailable";
        }

        public string PlayId { get; }
        public string? SxmTrackId { get; }
        public string Channel { get; }
        public string Artist { get; }
        public string Song { get; }
        public DateTime TimestampUtc { get; }
        public string? RecordingMusicBrainzId { get; }
        public string? Isrc { get; }
        public string? PlexRatingKey { get; }
        public string? PlexArtist { get; }
        public string? PlexTitle { get; }
        public string? PlexAlbum { get; }
        public string? PlexGuid { get; }
        public string MatchMethod { get; }
        public string Confidence { get; }
        public string? MbidMatchStatus { get; }
    }

    public class PlexTrackCacheRecord
    {
        public PlexTrackCacheRecord(
            string ratingKey,
            string? artist,
            string? title,
            string? album,
            string? guid,
            string matchMethod,
            string confidence)
        {
            RatingKey = ratingKey;
            Artist = artist;
            Title = title;
            Album = album;
            Guid = guid;
            MatchMethod = matchMethod;
            Confidence = confidence;
        }

        public string RatingKey { get; }
        public string? Artist { get; }
        public string? Title { get; }
        public string? Album { get; }
        public string? Guid { get; }
        public string MatchMethod { get; }
        public string Confidence { get; }
        public string? MbidMatchStatus { get; }
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

        public AlbumResolution(
            bool resolved,
            string? album,
            string? artistMusicBrainzId,
            string? albumMusicBrainzId,
            string? recordingMusicBrainzId = null,
            string? trackMusicBrainzId = null,
            string? isrc = null,
            string? resolutionMethod = null)
        {
            Resolved = resolved;
            Album = album;
            ArtistMusicBrainzId = artistMusicBrainzId;
            AlbumMusicBrainzId = albumMusicBrainzId;
            RecordingMusicBrainzId = recordingMusicBrainzId;
            TrackMusicBrainzId = trackMusicBrainzId;
            Isrc = isrc;
            ResolutionMethod = resolutionMethod;
        }

        public bool Resolved { get; }
        public string? Album { get; }
        public string? ArtistMusicBrainzId { get; }
        public string? AlbumMusicBrainzId { get; }
        public string? RecordingMusicBrainzId { get; }
        public string? TrackMusicBrainzId { get; }
        public string? Isrc { get; }
        public string? ResolutionMethod { get; }
    }

    public class PlexPlaylistStateRecord
    {
        public PlexPlaylistStateRecord(
            long listId,
            string playlistTitle,
            string playlistRatingKey,
            DateTime lastSyncUtc,
            IReadOnlyDictionary<string, PlexTrackCacheRecord> trackCache,
            IReadOnlyDictionary<string, string> userPlaylistKeys)
        {
            ListId = listId;
            PlaylistTitle = playlistTitle;
            PlaylistRatingKey = playlistRatingKey;
            LastSyncUtc = lastSyncUtc;
            TrackCache = trackCache;
            UserPlaylistKeys = userPlaylistKeys;
        }

        public long ListId { get; }
        public string PlaylistTitle { get; }
        public string PlaylistRatingKey { get; }
        public DateTime LastSyncUtc { get; }
        public IReadOnlyDictionary<string, PlexTrackCacheRecord> TrackCache { get; }

        // Plex Home username -> playlist ratingKey for the fan-out copies.
        public IReadOnlyDictionary<string, string> UserPlaylistKeys { get; }
    }
}
