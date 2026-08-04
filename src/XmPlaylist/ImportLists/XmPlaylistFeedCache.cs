using System;
using System.Collections.Concurrent;
using System.Threading;
using NzbDrone.Common.Http;

namespace XmPlaylist.ImportLists
{
    public static class XmPlaylistFeedCache
    {
        private static readonly ConcurrentDictionary<string, CachedResponse> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);

        public static HttpResponse Get(IHttpClient httpClient, HttpRequest request)
        {
            var key = request.Url.FullUri;

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
