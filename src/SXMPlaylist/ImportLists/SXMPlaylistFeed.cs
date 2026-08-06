using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// xmplaylist's feed shape. Parsed by the background worker when it captures a channel; the
    /// import lists no longer touch the feed directly.
    /// </summary>
    internal class XmFeedResponse
    {
        public int Count { get; set; }
        public List<XmPlayEntry>? Results { get; set; }
    }

    internal class XmPlayEntry
    {
        public string? Id { get; set; }
        public DateTime Timestamp { get; set; }
        public XmTrackInfo? Track { get; set; }
        [JsonProperty("channelId")]
        public string? ChannelId { get; set; }
        public List<XmLink>? Links { get; set; }
    }

    internal class XmTrackInfo
    {
        public string? Id { get; set; }
        public List<string>? Artists { get; set; }
        public string? Title { get; set; }
    }

    internal class XmLink
    {
        public string? Site { get; set; }
        public string? Url { get; set; }
    }
}
