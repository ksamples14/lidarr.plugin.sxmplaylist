using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace SXMPlaylist.ImportLists
{
    public class SXMPlaylistImportSettingsValidator : AbstractValidator<SXMPlaylistImportSettings>
    {
        public SXMPlaylistImportSettingsValidator()
        {
            RuleFor(c => c.Channel)
                .NotEmpty()
                .WithMessage("Channel is required");
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

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
