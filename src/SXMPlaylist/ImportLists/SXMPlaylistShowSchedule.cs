using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    public static class SXMPlaylistShowSchedule
    {
        public const string ChannelValue = "";

        private static readonly Dictionary<string, string[]> EpgKeyAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // xmplaylist uses williesroadhouse; SiriusXM EPG and channel-page show data use theroadhouse.
            ["williesroadhouse"] = new[] { "theroadhouse" }
        };

        private static readonly object EpgKeyCacheLock = new();
        private static readonly Dictionary<string, string> ResolvedEpgKeys = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<ShowInfo> Fetch(IHttpClient httpClient, string channel, string? channelName = null)
        {
            if (channel.IsNullOrWhiteSpace())
            {
                return new List<ShowInfo>();
            }

            var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cachedKey = GetCachedEpgKey(channel);
            if (cachedKey.IsNotNullOrWhiteSpace())
            {
                triedKeys.Add(cachedKey!);
                var cachedShows = FetchEpg(httpClient, cachedKey!);
                if (cachedShows.Count > 0)
                {
                    return cachedShows;
                }

                ClearCachedEpgKey(channel, cachedKey!);
            }

            foreach (var epgKey in GetCandidateEpgKeys(channel))
            {
                if (!triedKeys.Add(epgKey))
                {
                    continue;
                }

                var shows = FetchEpg(httpClient, epgKey);
                if (shows.Count > 0)
                {
                    CacheEpgKey(channel, epgKey);
                    return shows;
                }
            }

            foreach (var epgKey in FetchPageCandidateEpgKeys(httpClient, channelName))
            {
                if (!triedKeys.Add(epgKey))
                {
                    continue;
                }

                var shows = FetchEpg(httpClient, epgKey);
                if (shows.Count > 0)
                {
                    CacheEpgKey(channel, epgKey);
                    return shows;
                }
            }

            return new List<ShowInfo>();
        }

        private static IReadOnlyList<ShowInfo> FetchEpg(IHttpClient httpClient, string channel)
        {
            var url = "https://www.siriusxm.com/sxmepg/epg.sxmchepginfo.xmc" +
                      $"?channelKeys={Uri.EscapeDataString(channel)}&distribution=XMDCOM&tzone=Eastern";
            var request = SXMPlaylistRequestBuilder.Build(url);
            var response = httpClient.Get(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.IsNullOrWhiteSpace())
            {
                return new List<ShowInfo>();
            }

            return Parse(response.Content);
        }

        private static IEnumerable<string> GetCandidateEpgKeys(string channel)
        {
            if (EpgKeyAliases.TryGetValue(channel, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    yield return alias;
                }
            }

            yield return channel;
        }

        private static IEnumerable<string> FetchPageCandidateEpgKeys(IHttpClient httpClient, string? channelName)
        {
            var slug = ToSiriusXmSlug(channelName);
            if (slug.IsNullOrWhiteSpace())
            {
                yield break;
            }

            var request = SXMPlaylistRequestBuilder.Build($"https://www.siriusxm.com/channels/{slug}");
            var response = httpClient.Get(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.IsNullOrWhiteSpace())
            {
                yield break;
            }

            var normalized = response.Content
                .Replace("\\\"", "\"")
                .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0022", "\"", StringComparison.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patterns = new[]
            {
                "name\\s*=\\s*\"contentid\"[^>]*\\scontent\\s*=\\s*\"(?<id>[a-z0-9_-]+)\"",
                "\"name\"\\s*:\\s*\"contentid\".*?\"content\"\\s*:\\s*\"(?<id>[a-z0-9_-]+)\"",
                "\"channel_id\"\\s*:\\s*\"(?<id>[a-z0-9_-]+)\"",
                "\"channelId\"\\s*:\\s*\"(?<id>[a-z0-9_-]+)\""
            };

            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(normalized, pattern, RegexOptions.IgnoreCase))
                {
                    var key = match.Groups["id"].Value;
                    if (key.IsNotNullOrWhiteSpace() && seen.Add(key))
                    {
                        yield return key;
                    }
                }
            }
        }

        private static string? GetCachedEpgKey(string channel)
        {
            lock (EpgKeyCacheLock)
            {
                return ResolvedEpgKeys.TryGetValue(channel, out var epgKey) ? epgKey : null;
            }
        }

        private static void CacheEpgKey(string channel, string epgKey)
        {
            lock (EpgKeyCacheLock)
            {
                ResolvedEpgKeys[channel] = epgKey;
            }
        }

        private static void ClearCachedEpgKey(string channel, string epgKey)
        {
            lock (EpgKeyCacheLock)
            {
                if (ResolvedEpgKeys.TryGetValue(channel, out var cached) && string.Equals(cached, epgKey, StringComparison.OrdinalIgnoreCase))
                {
                    ResolvedEpgKeys.Remove(channel);
                }
            }
        }

        private static string ToSiriusXmSlug(string? channelName)
        {
            if (channelName.IsNullOrWhiteSpace())
            {
                return "";
            }

            var normalized = channelName!.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            var lastWasDash = false;

            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasDash = false;
                }
                else if (!lastWasDash && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasDash = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        public static IReadOnlyList<ShowInfo> Parse(string json)
        {
            var root = JObject.Parse(json);
            var epg = root["chEpgInfo"] ?? root;
            var programs = (epg["pg"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(p => new
                {
                    ProgramId = p["pgid"]?.Value<string>() ?? "",
                    Name = p["name"]?.Value<string>() ?? ""
                })
                .Where(p => p.ProgramId.IsNotNullOrWhiteSpace() && p.Name.IsNotNullOrWhiteSpace())
                .GroupBy(p => p.ProgramId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            var windowsByProgram = new Dictionary<string, List<ShowWindow>>(StringComparer.OrdinalIgnoreCase);

            foreach (var schedule in (epg["dayChSchedules"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var entries = (schedule["episode"] as JArray ?? schedule["schedules"] as JArray ?? new JArray()).OfType<JObject>();
                foreach (var entry in entries)
                {
                    var programId = entry["pgid"]?.Value<string>() ?? "";
                    var start = ParseEpgDate(entry["sc"]?["sTimeStr"]?.Value<string>() ?? entry["tm"]?.Value<string>() ?? entry["startTime"]?.Value<string>() ?? "");
                    var end = ParseEpgDate(entry["sc"]?["eTimeStr"]?.Value<string>() ?? entry["etm"]?.Value<string>() ?? entry["endTime"]?.Value<string>() ?? "");

                    if (programId.IsNullOrWhiteSpace() || start == null || end == null || end <= start)
                    {
                        continue;
                    }

                    if (!programs.ContainsKey(programId))
                    {
                        var name = entry["pr"]?["pName"]?.Value<string>() ?? entry["pr"]?["name"]?.Value<string>() ?? "";
                        if (name.IsNotNullOrWhiteSpace())
                        {
                            programs[programId] = name;
                        }
                    }

                    if (!windowsByProgram.TryGetValue(programId, out var windows))
                    {
                        windows = new List<ShowWindow>();
                        windowsByProgram[programId] = windows;
                    }

                    windows.Add(new ShowWindow(start.Value, end.Value));
                }
            }

            return windowsByProgram
                .Select(kvp => new ShowInfo(kvp.Key, programs.TryGetValue(kvp.Key, out var name) ? name : kvp.Key, kvp.Value))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static DateTime? ParseEpgDate(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            var normalized = value
                .Replace(" UTC", " +00:00", StringComparison.OrdinalIgnoreCase)
                .Replace(" EDT", " -04:00", StringComparison.OrdinalIgnoreCase)
                .Replace(" EST", " -05:00", StringComparison.OrdinalIgnoreCase);
            var formats = new[]
            {
                "MM.dd.yyyy HH:mm:ss zzz",
                "M.d.yyyy HH:mm:ss zzz",
                "MM.dd.yyyy H:mm:ss zzz",
                "M.d.yyyy H:mm:ss zzz",
                "MM.dd.yyyy HH:mm zzz",
                "M.d.yyyy HH:mm zzz",
                "MM.dd.yyyy H:mm zzz",
                "M.d.yyyy H:mm zzz"
            };

            if (DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact.UtcDateTime;
            }

            // The EPG is requested with tzone=Eastern (see FetchPageCandidateEpgKeys), so an
            // offset-less fallback string is Eastern time — never the host's local zone and never
            // UTC. A UTC/WSL host would otherwise mis-convert every window and mis-attribute plays
            // to shows. Convert Eastern explicitly (DST-aware); if the zone data is unavailable,
            // fail the parse rather than guess.
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                try
                {
                    var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    return TimeZoneInfo.ConvertTimeToUtc(parsed, eastern);
                }
                catch (TimeZoneNotFoundException)
                {
                    return null;
                }
                catch (InvalidTimeZoneException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    public class ShowInfo
    {
        public ShowInfo(string programId, string name, IReadOnlyList<ShowWindow> windows)
        {
            ProgramId = programId;
            Name = name;
            Windows = windows;
        }

        public string ProgramId { get; }
        public string Name { get; }
        public IReadOnlyList<ShowWindow> Windows { get; }
    }

    public class ShowWindow
    {
        public ShowWindow(DateTime startUtc, DateTime endUtc)
        {
            StartUtc = startUtc;
            EndUtc = endUtc;
        }

        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }

        public bool Contains(DateTime timestampUtc)
        {
            return timestampUtc >= StartUtc && timestampUtc < EndUtc;
        }
    }
}
