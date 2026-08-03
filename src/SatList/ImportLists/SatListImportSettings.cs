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
            RuleFor(c => c.ApiUrl)
                .NotEmpty()
                .WithMessage("API URL is required");

            RuleFor(c => c.ApiUrl)
                .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == "http" || uri.Scheme == "https"))
                .WithMessage("API URL must be a valid HTTP or HTTPS URL");
        }
    }

    public class SatListImportSettings : IImportListSettings
    {
        private static readonly SatListImportSettingsValidator Validator = new();

        public SatListImportSettings()
        {
            BaseUrl = "";
            ApiUrl = "";
        }

        [FieldDefinition(0, Label = "API URL", HelpText = "Full URL of the JSON endpoint that returns your import list")]
        public string ApiUrl { get; set; }

        [FieldDefinition(1, Label = "API Key", HelpText = "API key to include as a query parameter or header (optional)", Type = FieldType.Password)]
        public string ApiKey { get; set; }

        [FieldDefinition(2, Label = "API Key Location", HelpText = "Where to send the API key", Type = FieldType.Select, SelectOptions = typeof(ApiKeyLocation))]
        public int ApiKeyLocation { get; set; }

        [FieldDefinition(3, Label = "API Key Parameter Name", HelpText = "Name of the query parameter or header (default: api_key)", Advanced = true)]
        public string ApiKeyParameterName { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum ApiKeyLocation
    {
        Query,
        Header
    }
}
