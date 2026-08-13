using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace SXMPlaylist.ImportLists
{
    public enum ReleasePriorityMode
    {
        // Keep Singles at 0 so existing saved list settings that lack this field deserialize to
        // the historical singles-first behavior.
        Singles = 0,
        Albums = 1
    }

    public class SXMPlaylistImportSettingsValidator : AbstractValidator<SXMPlaylistImportSettings>
    {
        public SXMPlaylistImportSettingsValidator()
        {
            RuleFor(c => c.Channel)
                .NotEmpty()
                .WithMessage("Channel is required");

            RuleFor(c => c.MinimumPlays)
                .GreaterThanOrEqualTo(1);

            RuleFor(c => c.HistoryRetentionDays)
                .InclusiveBetween(1, (int)SXMPlaylistHistoryStore.PlayRetention.TotalDays);

            RuleFor(c => c.AlbumsPerDay)
                .InclusiveBetween(0, 500);
        }
    }

    public class SXMPlaylistImportSettings : IImportListSettings
    {
        private static readonly SXMPlaylistImportSettingsValidator Validator = new();

        public const string PluginName = "SXM Playlist";

        public SXMPlaylistImportSettings()
        {
            BaseUrl = "https://xmplaylist.com";
            Channel = "";
            Show = SXMPlaylistShowSchedule.ChannelValue;
            MinimumPlays = 1;
            HistoryRetentionDays = 1;
            AlbumsPerDay = 24;
            RequireMusicBrainzId = false;
            ReleasePriority = ReleasePriorityMode.Singles;
            AddCompanionPlexPlaylist = false;
        }

        // Lidarr's dynamic Select field renders as a multi-select checklist or a single dropdown
        // based purely on whether the bound value is an array at runtime (EnhancedSelectInput.js:
        // `isMultiSelect = Array.isArray(value)`), not on the field type - a plain scalar string
        // here is what gives a real single-pick dropdown.
        public string Channel { get; set; }

        // Lidarr only refetches dynamic select options when baseUrl/apiPath/apiKey change
        // (EnhancedSelectInputConnector.importantFieldNames). Bind the Channel UI to apiPath so
        // the Show dropdown refreshes when a user picks a different channel.
        [FieldDefinition(0, Label = "Channel", Type = FieldType.Select, SelectOptionsProviderAction = "getChannels", HelpText = "SiriusXM channel to import plays from. Multiple import lists can target the same channel if each selects a different show.")]
        public string ApiPath
        {
            get => Channel;
            set => Channel = value;
        }

        [FieldDefinition(1, Label = "Show", Type = FieldType.Select, SelectOptionsProviderAction = "getShows", HelpText = "Optional show filter from the SiriusXM EPG schedule. Leave blank to import all plays from the entire channel.")]
        public string Show { get; set; }

        [FieldDefinition(2, Label = "Require MusicBrainz ID", Type = FieldType.Checkbox, HelpText = "Only import albums that have a MusicBrainz album ID at import time. Albums without an ID may be retried in the background and imported later if one is found.")]
        public bool RequireMusicBrainzId { get; set; }

        [FieldDefinition(3, Label = "Minimum Plays", Type = FieldType.Number, HelpText = "Minimum number of times a track must have been played in the channel history before its album is imported.")]
        public int MinimumPlays { get; set; }

        [FieldDefinition(4, Label = "Albums Per Day", Type = FieldType.Number, HelpText = "Maximum number of albums added per day, spread evenly across 24 hours. Set to 0 for unlimited.")]
        public int AlbumsPerDay { get; set; }

        [FieldDefinition(5, Label = "Release Priority", Type = FieldType.Select, SelectOptions = typeof(ReleasePriorityMode), HelpText = "When both a single and an album release exist for a track, prefer importing one over the other.")]
        public ReleasePriorityMode ReleasePriority { get; set; }

        [FieldDefinition(6, Label = "Companion Plex Playlist", Type = FieldType.Checkbox, HelpText = "Create and maintain a Plex playlist mirroring this import list's plays. Requires a Plex server configured in Settings > Connect.", HelpTextWarning = "Set up Plex in Settings > Connect")]
        public bool AddCompanionPlexPlaylist { get; set; }

        [FieldDefinition(7, Label = "Plex Playlist History Days", Type = FieldType.Number, HelpText = "Number of days of play history to include in the companion Plex playlist.")]
        public int HistoryRetentionDays { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
