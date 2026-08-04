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
            RuleFor(c => c.ResultCount)
                .InclusiveBetween(1, 1000)
                .WithMessage("Result count must be between 1 and 1000");

            RuleFor(c => c.Channel)
                .NotEmpty()
                .When(c => c.ListMode == (int)XmPlaylistListMode.Channel)
                .WithMessage("Channel is required when the list mode is set to a specific channel");
        }
    }

    public class XmPlaylistImportSettings : IImportListSettings
    {
        private static readonly XmPlaylistImportSettingsValidator Validator = new();

        public XmPlaylistImportSettings()
        {
            BaseUrl = "https://xmplaylist.com";
            ListMode = (int)XmPlaylistListMode.Feed;
            Channel = "";
            ImportType = (int)XmPlaylistImportType.Artists;
            ResultCount = 200;
            DedupeArtists = true;
            OnlyNewArtists = true;
        }

        [FieldDefinition(0, Label = "List Mode", HelpText = "How the import list should be built from xmplaylist data", Type = FieldType.Select, SelectOptions = typeof(XmPlaylistListMode))]
        public int ListMode { get; set; }

        [FieldDefinition(1, Label = "Channel", HelpText = "SiriusXM channel ID to pull plays from (e.g. altnation, xmu, thespectrum). Used when List Mode is set to 'Specific Channel'.", Hidden = HiddenType.HiddenIfNotSet)]
        public string Channel { get; set; }

        [FieldDefinition(2, Label = "Channel Filter", HelpText = "Optional comma-separated channel IDs to restrict results to when List Mode is 'Recent Plays' (e.g. altnation, xmu). Leave empty for all channels.", Advanced = true)]
        public string? ChannelFilter { get; set; }

        [FieldDefinition(3, Label = "Import Type", HelpText = "What to import for each play found in the feed", Type = FieldType.Select, SelectOptions = typeof(XmPlaylistImportType))]
        public int ImportType { get; set; }

        [FieldDefinition(4, Label = "Result Count", HelpText = "Number of recent plays to fetch (1-1000, default 200)", Type = FieldType.Number)]
        public int ResultCount { get; set; }

        [FieldDefinition(5, Label = "Dedupe Artists", HelpText = "Only return each unique artist once per fetch (recommended to avoid duplicates)", Type = FieldType.Checkbox)]
        public bool DedupeArtists { get; set; }

        [FieldDefinition(6, Label = "Only New Artists", HelpText = "Only emit artists not seen in a previous refresh. Each list tracks its own seen-artists state on disk, so artists are added once and not re-imported on subsequent polls.", Type = FieldType.Checkbox)]
        public bool OnlyNewArtists { get; set; }

        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public enum XmPlaylistListMode
    {
        [FieldOption(Label = "Recent Plays (All Channels)")]
        Feed = 0,

        [FieldOption(Label = "Specific Channel")]
        Channel = 1
    }

    public enum XmPlaylistImportType
    {
        [FieldOption(Label = "Artists")]
        Artists = 0,

        [FieldOption(Label = "Albums")]
        Albums = 1,

        [FieldOption(Label = "Artists and Albums")]
        ArtistsAndAlbums = 2
    }
}
