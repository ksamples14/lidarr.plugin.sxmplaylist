using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistImportSettingsValidator : AbstractValidator<XmPlaylistImportSettings>
    {
        public XmPlaylistImportSettingsValidator()
        {
            RuleFor(c => c.Channel)
                .Must(c => c != null && c.Count() == 1)
                .WithMessage("Select exactly one channel");
        }
    }

    public class XmPlaylistImportSettings : IImportListSettings
    {
        private static readonly XmPlaylistImportSettingsValidator Validator = new();

        public XmPlaylistImportSettings()
        {
            BaseUrl = "https://xmplaylist.com";
            Channel = new List<string>();
        }

        // Lidarr's dynamic Select field (SelectOptionsProviderAction) only has precedent as a
        // multi-select IEnumerable in Lidarr's own codebase - there's no scalar single-value
        // usage anywhere to model this on. Modeled as a collection and validated down to exactly
        // one selection, rather than risk guessing at an unproven single-value binding.
        [FieldDefinition(0, Label = "Channel", Type = FieldType.Select, SelectOptionsProviderAction = "getChannels", HelpText = "SiriusXM channel to pull plays from. One list tracks one channel.")]
        public IEnumerable<string> Channel { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
