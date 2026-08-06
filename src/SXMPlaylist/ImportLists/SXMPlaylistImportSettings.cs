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
        }

        // Lidarr's dynamic Select field renders as a multi-select checklist or a single dropdown
        // based purely on whether the bound value is an array at runtime (EnhancedSelectInput.js:
        // `isMultiSelect = Array.isArray(value)`), not on the field type - a plain scalar string
        // here is what gives a real single-pick dropdown.
        [FieldDefinition(0, Label = "Channel", Type = FieldType.Select, SelectOptionsProviderAction = "getChannels", HelpText = "SiriusXM channel to pull plays from. One list tracks one channel.")]
        public string Channel { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
