# AGENTS.md

## Project overview

- This repository contains **Lidarr.Plugin.SXMPlaylist**, a .NET 8 C# plugin for Lidarr (nightly branch only).
- It monitors SiriusXM channel play feeds via xmplaylist.com and automatically adds played artists to the user's Lidarr library. One import list per channel; a background worker captures feeds, resolves songs to albums (Deezer/MusicBrainz/Apple Music), and the import lists present resolved tracks to Lidarr hourly. Optional companion Plex playlists mirror recent plays.
- Production code is under `src/SXMPlaylist/` (single project, merged into one DLL via ILRepack).
- Tests are under `tests/SXMPlaylist.Tests/` (a hand-rolled executable harness — **not** a test framework; see Testing).
- `lib/` holds version-matched Lidarr assemblies (`Lidarr.Common.dll`, `Lidarr.Core.dll`, `Lidarr.Http.dll`, `System.Data.SQLite.dll`) that the plugin compiles against. **Do not update or replace these unless the running Lidarr nightly version changes.**
- `Submodules/Lidarr/` is the Lidarr source as a git submodule — reference only, never edit or build against it.
- `PLAN.md` and `PLAN-*.md` are private working notes, **gitignored** — never commit them, but do read them for architecture decisions, accepted risks, and the deploy recipe (§8).

## Setup

- Restore with:
  `dotnet restore SXMPlaylist.sln`
- Build the solution with:
  `dotnet build SXMPlaylist.sln -c Release`
  (`-p:EnableAnalyzers=false` is a historical no-op — the current csproj does not import Lidarr's build props and wires no analyzers; do not add it back.)
- Requires the .NET 8 SDK. There is no CI, and the csproj wires **no** StyleCop/analyzer packages — a clean build means compiler-clean, not analyzer-clean.
- Do not add or update NuGet packages unless the task requires it. Keep FluentValidation at the host-matching version (9.x), Newtonsoft.Json 13.0.4, NLog 5.x.
- Never commit credentials, connection strings, certificates, or generated secrets. API keys live in the user's `~/.hermes/.env` / Lidarr settings, not in this repo.

## Code conventions

- Follow `.editorconfig` and the repository's existing style: 4-space indent, `Allman` braces, `var` where the type is apparent, UTF-8.
- Naming: methods `VerbNoun` (`ResolveDueTracks`), private fields `_camelCase`, constants `PascalCase`, XML doc comments on public members. Classes live in `SXMPlaylist.ImportLists`.
- Nullable reference types are enabled — do not suppress warnings without justification.
- **SQL is always parameterized.** Every value-bearing SQL token goes through `command.Parameters.AddWithValue("...")`. Never interpolate data into SQL strings; only constant identifiers (table/column names passed to `EnsureColumn`) are interpolated, and only from literals. New queries must follow this.
- SQLite access goes exclusively through `SXMPlaylistHistoryStore` (WAL mode, `using`-scoped connections/commands/transactions, `DateTime` stored as `"O"` round-trip UTC strings). Schema changes use idempotent `EnsureColumn` plus `SchemaInfo`-flagged backfills — never bare `CREATE TABLE`/`ALTER TABLE` without the migration guard.
- The worker/resolver are **synchronous by design** — Lidarr's `IHttpClient.Get` is sync, and the MusicBrainz 1 req/s throttle (`MusicBrainzGate` + `ThrottleMusicBrainz`) is intentional `Thread.Sleep`-based serialization. Do not convert to async or use `.Result`/`.Wait()` wrappers; do not bypass the throttle.
- JSON is parsed with Newtonsoft `JToken`/`JsonConvert` using `?.` chains (feeds and APIs routinely omit fields — never assume a token exists).
- Logging is NLog through the injected `Logger` (`_logger.Debug/Warn/Error`). **Never log URLs that carry query-string credentials** (Plex tokens are appended to request URLs in `SXMPlaylistPlexClient` — strip before logging; see `ExecuteOnce`).
- Background work is best-effort: per-channel and per-track try/catch isolation in the worker loop so one failure never aborts the whole pass. Catch expected failure modes explicitly (HTTP/JSON errors); let unexpected exceptions surface to the loop's error handler rather than swallowing them at Debug level.
- Add comments only when they explain intent or non-obvious behavior (the existing code documents *why* well — match that).

## Architecture

- Data flow: `SXMPlaylistWorker` (background loop) captures feeds and resolves albums → `SXMPlaylistHistoryStore` (SQLite, source of truth) → `SXMPlaylistImport.Fetch()` (queries the DB only — no live feed calls on the Lidarr sync path) → Lidarr.
- Keep domain logic independent from infrastructure: resolution lives in `SXMPlaylistAlbumResolver`, Plex in `SXMPlaylistPlexClient`, EPG in `SXMPlaylistShowSchedule`, HTTP caching in `SXMPlaylistFeedCache`.
- Do not access the database directly from import-list settings/UI code paths; route through the store. Do not introduce a new architectural pattern without explaining why it is necessary.
- Place new files in the project and namespace that owns the behavior (`SXMPlaylist.ImportLists`).
- Be aware of accepted design trade-offs (documented in PLAN.md): `Tracks` is keyed by `TrackId` alone (cross-channel collision accepted), multi-artist tracks credit the primary artist, and `api.lidarr.audio` 500s are accepted as self-healing.

## Efficiency

- Avoid repeated enumeration of `IEnumerable<T>`; materialize once when iterating multiple times.
- Avoid unnecessary allocations in frequently executed code — e.g. do not allocate `new[] { ... }` per track in a loop; hoist to `static readonly` (the resolver already exposes `ReleasePriorities` — reuse it).
- MusicBrainz calls are rate-limited to 1 req/s globally — every new resolver call adds to that budget. Prefer reusing already-fetched responses (Deezer's album title, cached feed pages) over extra round-trips.
- In-process caches (`SXMPlaylistFeedCache`, Plex track cache) must be bounded or TTL'd — do not add unbounded static collections.
- Preserve thread safety: the static `MusicBrainzGate` semaphore serializes MB access; worker lifecycle state is `_lifecycleLock`-guarded. Do not add unsynchronized mutable statics.
- Prefer measured improvements over speculative micro-optimizations.

## Testing

After making changes, run:

1. `dotnet build SXMPlaylist.sln -c Release`
2. `dotnet run --project tests/SXMPlaylist.Tests/SXMPlaylist.Tests.csproj`

**Important: the test suite is a hand-rolled executable harness, not xunit/nunit. `dotnet test` finds zero tests and reports success while running nothing — never use it as the test gate.** The harness is `tests/SXMPlaylist.Tests/Program.cs`: a `Main()` that calls `Test*` methods sequentially and prints `[PASS]`/`[FAIL]`, exiting non-zero on failure. Add tests as new `private static void Test...()` methods called from `Main()`.

- Mock HTTP with the existing `FakeHttpClient.Respond(urlFragment, json)` pattern (URL-substring matching; give each test its own `FakeHttpClient` instance when multiple calls are involved).
- Add or update tests when behavior changes. Cover success, failure, boundary, and null cases. The resolver, store, worker, backfill, and Plex client all have established test patterns to extend.
- Do not weaken or delete tests merely to make a change pass.
- `dotnet format --verify-no-changes` is not a useful gate in this repo today: the `.editorconfig` demands CRLF but the working tree is LF (no `.gitattributes`), so it reports ~8k ENDOFLINE violations that are pre-existing and unrelated to any change. If you run it, ignore ENDOFLINE and check for anything else; do not "fix" line endings as part of an unrelated change.

## Code review rules

When reviewing code, prioritize:

1. Correctness and possible runtime defects
2. Security and data-loss risks (credentials in logs, SQL injection, timestamp/retention bugs)
3. Async, concurrency, and resource-lifetime problems (SQLite busy handling, cancellation, disposal)
4. Performance problems with credible impact (unbounded caches, missing indexes, per-row connections)
5. Maintainability and consistency

For each finding:

- Give the file path and line number.
- Explain the concrete impact.
- Recommend a specific fix.
- Distinguish confirmed defects from optional improvements.
- Do not report formatting issues already enforced by automated tooling, and do not report StyleCop default rules that conflict with this repo's established style (`this.`-prefix, usings outside namespace, `_`-prefixed fields are deliberate).

## Change discipline

- Inspect related code and tests before editing (the resolver/history-store tests encode the intended behavior).
- Make the smallest coherent change that solves the requested problem.
- Preserve unrelated user changes.
- Do not perform broad renames, formatting passes, or refactors unless requested.
- Do not modify generated files (`obj/`, `bin/`, ILRepack outputs), `lib/*.dll`, or anything under `Submodules/`.
- Do not commit `PLAN.md` / `PLAN-*.md` (gitignored working notes).
- Update `README.md` when externally visible behavior changes (settings fields, resolution pipeline, Plex features).

## Completion requirements

Before declaring the task complete:

- Confirm the solution builds: `dotnet build SXMPlaylist.sln -c Release` succeeds.
- Confirm the harness passes: `dotnet run --project tests/SXMPlaylist.Tests/SXMPlaylist.Tests.csproj` prints `ALL TESTS PASSED`.
- Summarize the files changed and the resulting behavior.
- Report any warnings, failed checks, assumptions, or remaining risks.
- If deployment to the live `lidarr-1` container is in scope, follow the recipe in PLAN.md §8 (scp DLL + deps.json, `chown nobody:users`, `docker restart lidarr-1`) and verify the plugin loads via `docker exec lidarr-1 grep SXMPlaylistPlugin /config/logs/lidarr.txt`.
