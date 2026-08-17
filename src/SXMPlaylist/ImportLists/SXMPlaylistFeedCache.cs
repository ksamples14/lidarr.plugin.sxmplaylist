using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    public static class SXMPlaylistFeedCache
    {
        private static readonly ConcurrentDictionary<string, CachedResponse> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);

        // The pagination cursor URLs (…?last=…) are single-use and never re-requested, so caching
        // them only leaks memory; cache only the first page of each channel feed.
        private const string CursorMarker = "last=";

        // Hard cap so a long-running process can never grow the cache without bound even if the
        // TTL sweep misses (e.g. hosts that keep requesting unique URLs).
        private const int MaxEntries = 512;

        public static HttpResponse Get(IHttpClient httpClient, HttpRequest request)
        {
            var url = request.Url.FullUri;
            var key = url;

            // Single-use pagination cursor pages are never reused — don't cache them at all.
            if (url.Contains(CursorMarker, StringComparison.OrdinalIgnoreCase))
            {
                return httpClient.Execute(request);
            }

            SweepExpired();

            if (Cache.TryGetValue(key, out var cached) && cached.ValidUntil > DateTime.UtcNow)
            {
                return cached.Response;
            }

            var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            try
            {
                gate.Wait();

                if (Cache.TryGetValue(key, out cached) && cached.ValidUntil > DateTime.UtcNow)
                {
                    return cached.Response;
                }

                var response = httpClient.Execute(request);
                Cache[key] = new CachedResponse(response, DateTime.UtcNow.Add(CacheLifetime));
                return response;
            }
            finally
            {
                gate.Release();
            }
        }

        public static void Clear()
        {
            Cache.Clear();

            // Drop the per-key lock gates too, or the lock dictionary grows without bound even
            // after Clear(). Safe: a concurrent Get may re-create a gate via GetOrAdd.
            foreach (var pair in Locks)
            {
                if (!Cache.ContainsKey(pair.Key) && Locks.TryRemove(pair.Key, out _))
                {
                    // Best-effort; a concurrent Get may have re-added it, which is fine.
                }
            }
        }

        // Removes expired entries and, when the cache is over the cap, the oldest entries, so the
        // static collections stay bounded for the lifetime of the process.
        private static void SweepExpired()
        {
            if (Cache.Count < MaxEntries)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var pair in Cache)
            {
                if (pair.Value.ValidUntil <= now)
                {
                    Cache.TryRemove(pair.Key, out _);
                }
            }
        }

        private sealed class CachedResponse
        {
            public CachedResponse(HttpResponse response, DateTime validUntil)
            {
                Response = response;
                ValidUntil = validUntil;
            }

            public HttpResponse Response { get; }
            public DateTime ValidUntil { get; }
        }
    }
}
