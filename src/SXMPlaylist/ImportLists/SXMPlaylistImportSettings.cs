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

            RuleFor(c => c.AlbumsPerHour)
                .InclusiveBetween(1, 100);
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
            HistoryRetentionDays = (int)SXMPlaylistHistoryStore.PlayRetention.TotalDays;
            AlbumsPerHour = 20;
            RequireMusicBrainzId = false;
            ReleasePriority = ReleasePriorityMode.Singles;
        }

        // Lidarr's dynamic Select field renders as a multi-select checklist or a single dropdown
        // based purely on whether the bound value is an array at runtime (EnhancedSelectInput.js:
        // `isMultiSelect = Array.isArray(value)`), not on the field type - a plain scalar string
        // here is what gives a real single-pick dropdown.
        public string Channel { get; set; }

        // Lidarr only refetches dynamic select options when baseUrl/apiPath/apiKey change
        // (EnhancedSelectInputConnector.importantFieldNames). Bind the Channel UI to apiPath so
        // the Show dropdown refreshes when a user picks a different channel.
        [FieldDefinition(0, Label = "Channel", Type = FieldType.Select, SelectOptionsProviderAction = "getChannels", HelpText = "SiriusXM channel to pull plays from. Multiple lists can use the same channel when each selects a different show.")]
        public string ApiPath
        {
            get => Channel;
            set => Channel = value;
        }

        [FieldDefinition(1, Label = "Show", Type = FieldType.Select, SelectOptionsProviderAction = "getShows", HelpText = "Optional SiriusXM show filter from the official EPG schedule. Channel imports the whole channel.")]
        public string Show { get; set; }

        [FieldDefinition(2, Label = "Require MusicBrainz ID", Type = FieldType.Checkbox, HelpText = "Only import albums with a MusicBrainz album ID. Title-only matches can still appear later if background retries find an ID.")]
        public bool RequireMusicBrainzId { get; set; }

        [FieldDefinition(3, Label = "Minimum Plays", Type = FieldType.Number, HelpText = "Minimum number of times a song must appear in the retained channel history before it is imported.")]
        public int MinimumPlays { get; set; }

        [FieldDefinition(4, Label = "History Retention Days", Type = FieldType.Number, HelpText = "How many days of captured plays to keep and consider for this list.")]
        public int HistoryRetentionDays { get; set; }

        [FieldDefinition(5, Label = "Albums Per Hour", Type = FieldType.Number, HelpText = "Maximum albums this list presents each hourly import-list sync.")]
        public int AlbumsPerHour { get; set; }

        [FieldDefinition(6, Label = "Release Priority", Type = FieldType.Select, SelectOptions = typeof(ReleasePriorityMode), HelpText = "Prefer Singles for exact radio releases, or Albums for older/classic channels where the album is usually the desired add.")]
        public ReleasePriorityMode ReleasePriority { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
