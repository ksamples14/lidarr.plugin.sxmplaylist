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
                .NotEmpty()
                .WithMessage("Channel is required (e.g. altnation, xmu, thespectrum)");
        }
    }

    public class XmPlaylistImportSettings : IImportListSettings
    {
        private static readonly XmPlaylistImportSettingsValidator Validator = new();

        public XmPlaylistImportSettings()
        {
            BaseUrl = "https://xmplaylist.com";
            Channel = "";
        }

        [FieldDefinition(0, Label = "Channel", HelpText = "SiriusXM channel ID to pull plays from (e.g. altnation, xmu, thespectrum). One list tracks one channel.")]
        public string Channel { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
