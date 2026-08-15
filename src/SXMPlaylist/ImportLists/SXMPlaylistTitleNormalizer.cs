using System.Text.RegularExpressions;

namespace SXMPlaylist.ImportLists
{
    internal static class SXMPlaylistTitleNormalizer
    {
        // Edition qualifiers stripped before search/scoring so provider titles can match clean
        // library/MusicBrainz titles.
        private static readonly Regex EditionSuffixPattern = new(
            @"[\(\[\{]\s*(deluxe|remaster(ed)?|edition|anniversary|special|expanded|bonus|complete|acoustic|live|demo|radio edit|extended|instrumental|mono|stereo|explicit|clean|version|single|promo)\b[^\)\]\}]*[\)\]\}]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // SiriusXM/xmplaylist annotate many titles with a year in parens, e.g. "Emergency (85)".
        // Anchored to the end so mid-title parenthesized numbers are left alone.
        private static readonly Regex YearSuffixPattern = new(
            @"[\(\[\{]\s*(?:(?:19|20)\d{2}|\d{2})\s*[\)\]\}]\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string StripEditionAndYearSuffixes(string value)
        {
            var stripped = EditionSuffixPattern.Replace(value, " ");
            return YearSuffixPattern.Replace(stripped, " ").Trim();
        }

        public static string StripTrailingParentheticalSuffixes(string value)
        {
            var result = Regex.Replace(value, @"\s*\([^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s*\([^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(result, @"\s*\[[^\]]*\]\s*$", "", RegexOptions.IgnoreCase);
        }
    }
}
