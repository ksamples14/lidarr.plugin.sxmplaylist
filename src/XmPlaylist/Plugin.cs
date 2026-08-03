using NLog;
using NLog.Config;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Plugins;

namespace XmPlaylist
{
    public class XmPlaylistPlugin : Plugin,
        IHandle<ApplicationStartingEvent>
    {
        public override string Name => PluginInfo.Name;
        public override string Owner => PluginInfo.Author;
        public override string GithubUrl => PluginInfo.RepoUrl;

        public XmPlaylistPlugin()
        {
        }

        public void Handle(ApplicationStartingEvent message)
        {
            var config = LogManager.Configuration;
            if (config != null)
            {
                var rule = new LoggingRule($"XmPlaylist.*", LogLevel.Debug, config.FindTargetByName("file"));
                if (!config.LoggingRules.Contains(rule))
                {
                    config.LoggingRules.Add(rule);
                    LogManager.Configuration = config;
                }
            }
        }
    }
}
