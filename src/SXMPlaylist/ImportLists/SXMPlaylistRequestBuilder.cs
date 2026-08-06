using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    internal static class SXMPlaylistRequestBuilder
    {
        public static HttpRequest Build(string url)
        {
            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add("User-Agent", "SXMPlaylist-Lidarr-Plugin/1.0");
            return request;
        }
    }
}
