using System;
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

        // YearSuffixPattern matches 2-digit or full-year parens anchored at the end, e.g. "Emergency (85)".
        private static readonly Regex YearSuffixPattern = new(
            @"[\(\[\{]\s*(?:(?:19|20)\d{2}|\d{2})\s*[\)\]\}]\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

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

        // Title comparison for matching a feed/SXM title against a library track title
        // (Lidarr backfill, coverage gate). Strips edition/year suffixes ("Shattered Dreams (88)"
        // -> "Shattered Dreams"), folds curly quotes/apostrophes to straight ("Hangin’ On" ->
        // "Hangin' On"), lowercases, and collapses whitespace — so versioned or punctuation-
        // variant titles still match the canonical library title. Exact-match comparisons that
        // don't do this silently miss real library copies (observed 2026-08-18: "(88)" and
        // curly-apostrophe titles left rows un-backfilled and uncovered).
        public static bool TitlesEqual(string? a, string? b)
        {
            return string.Equals(NormalizeForComparison(a), NormalizeForComparison(b), StringComparison.Ordinal);
        }

        public static string NormalizeForComparison(string? value)
        {
            if (value == null)
            {
                return "";
            }

            // Curly quotes/apostrophes (U+2018/2019/201C/201D) -> straight ASCII.
            var folded = value
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201C', '"')
                .Replace('\u201D', '"');

            var stripped = StripEditionAndYearSuffixes(folded);
            return WhitespacePattern.Replace(stripped.Trim().ToLowerInvariant(), " ");
        }
    }
}
