using NLog;
using NLog.Config;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Plugins;
using XmPlaylist.ImportLists;

namespace XmPlaylist
{
    public class XmPlaylistPlugin : Plugin,
        IHandle<ApplicationStartingEvent>,
        IHandle<ApplicationStartedEvent>,
        IHandle<ApplicationShutdownRequested>
    {
        public override string Name => PluginInfo.Name;
        public override string Owner => PluginInfo.Author;
        public override string GithubUrl => PluginInfo.RepoUrl;

        private readonly XmPlaylistWorker _worker;

        // Constructor injection into the plugin class is the established pattern (see Tubifarry):
        // DryIoc auto-registers every concrete plugin type and resolves it with its dependencies.
        public XmPlaylistPlugin(
            IHttpClient httpClient,
            IAppFolderInfo appFolderInfo,
            IImportListFactory importListFactory,
            Logger logger)
        {
            _worker = new XmPlaylistWorker(httpClient, appFolderInfo, importListFactory, logger);
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

        // Started once Lidarr is fully up (DB + services ready), so channel discovery works.
        public void Handle(ApplicationStartedEvent message)
        {
            _worker.Start();
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _worker.Stop();
        }
    }
}
