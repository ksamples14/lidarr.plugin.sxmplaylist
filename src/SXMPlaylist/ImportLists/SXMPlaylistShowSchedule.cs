using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    public static class SXMPlaylistShowSchedule
    {
        public const string ChannelValue = "";

        public static IReadOnlyList<ShowInfo> Fetch(IHttpClient httpClient, string channel)
        {
            if (channel.IsNullOrWhiteSpace())
            {
                return new List<ShowInfo>();
            }

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
                "MM.dd.yyyy HH:mm zzz",
                "M.d.yyyy HH:mm zzz",
                "MM.dd.yyyy H:mm zzz",
                "M.d.yyyy H:mm zzz"
            };

            if (DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact.UtcDateTime;
            }

            return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
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
