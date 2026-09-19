using System.Globalization;
using System.Text.RegularExpressions;

namespace PetGPT.Reactions;

public sealed record ParsedReactionMarker(
    string Epoch,
    string PetId,
    string ReactionId,
    int? Intensity);

public static class ReactionProtocol
{
    public const int MaximumHrefLength = 192;

    private static readonly Regex Pattern = new(
        "\\Ahttps://petgpt\\.invalid/#r1/([0-9a-f]{16})/([a-z][a-z0-9_]{0,31})/([a-z][a-z0-9_]{0,31})(?:/(0|[1-9][0-9]{0,2}))?/end\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool TryParse(string? rawHref, out ParsedReactionMarker? marker)
    {
        marker = null;
        if (string.IsNullOrEmpty(rawHref) ||
            rawHref.Length > MaximumHrefLength ||
            rawHref.Any(character => character > 0x7f))
        {
            return false;
        }

        var match = Pattern.Match(rawHref);
        if (!match.Success)
            return false;

        int? intensity = null;
        if (match.Groups[4].Success)
        {
            if (!int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed > 100)
            {
                return false;
            }
            intensity = parsed;
        }

        marker = new ParsedReactionMarker(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            intensity);
        return true;
    }
}
