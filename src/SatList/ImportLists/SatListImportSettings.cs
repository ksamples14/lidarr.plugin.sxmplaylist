using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace SatList.ImportLists
{
    public class SatListImportSettingsValidator : AbstractValidator<SatListImportSettings>
    {
        public SatListImportSettingsValidator()
        {
            RuleFor(c => c.ResultCount)
                .InclusiveBetween(1, 1000)
                .WithMessage("Result count must be between 1 and 1000");
        }
    }

    public class SatListImportSettings : IImportListSettings
    {
        private static readonly SatListImportSettingsValidator Validator = new();

        public SatListImportSettings()
        {
            BaseUrl = "https://xmplaylist.com";
            ResultCount = 200;
        }

        [FieldDefinition(0, Label = "Result Count", HelpText = "Number of recent plays to fetch (1-1000, default 200)", Type = FieldType.Number)]
        public int ResultCount { get; set; }

        [FieldDefinition(1, Label = "Channel Filter", HelpText = "Comma-separated channel IDs to filter by (e.g. altnation, xmu, thespectrum). Leave empty for all channels.", Advanced = true)]
        public string ChannelFilter { get; set; }

        [FieldDefinition(2, Label = "Dedupe Artists", HelpText = "Only return each unique artist once (recommended to avoid duplicates)", Type = FieldType.Checkbox)]
        public bool DedupeArtists { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
