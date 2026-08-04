using NzbDrone.Common.Http;

namespace XmPlaylist.ImportLists
{
    internal static class XmPlaylistRequestBuilder
    {
        public static HttpRequest Build(string url)
        {
            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add("User-Agent", "XmPlaylist-Lidarr-Plugin/1.0");
            return request;
        }
    }
}
