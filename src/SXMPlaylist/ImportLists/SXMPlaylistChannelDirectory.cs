using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// xmplaylist's own frontend derives its channel picker from `/api/station` (no channel
    /// suffix - distinct from the per-channel `/api/station/{channel}` play endpoint). It's the
    /// only place the full SiriusXM channel lineup (deeplink/name/number) is available; free,
    /// unauthenticated, same as the other endpoints this plugin uses.
    /// </summary>
    public static class SXMPlaylistChannelDirectory
    {
        public static IReadOnlyList<ChannelInfo> Fetch(IHttpClient httpClient, string baseUrl)
        {
            var request = SXMPlaylistRequestBuilder.Build($"{baseUrl.TrimEnd('/')}/api/station");
            var response = httpClient.Get(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.IsNullOrWhiteSpace())
            {
                return new List<ChannelInfo>();
            }

            var json = JObject.Parse(response.Content);
            var results = json["results"] as JArray ?? new JArray();

            return results
                .Select(r => new ChannelInfo(
                    r["deeplink"]?.Value<string>() ?? "",
                    r["name"]?.Value<string>() ?? "",
                    r["number"]?.Value<string>()))
                .Where(c => c.Deeplink.IsNotNullOrWhiteSpace())
                .ToList();
        }
    }
}
