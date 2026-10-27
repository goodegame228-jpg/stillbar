using System.Text.RegularExpressions;

namespace Stillbar.Core.Lyrics;

public static partial class Artists
{
    // ", "  " & "  " и "  " x "  " feat. "  " ft. "  " при уч. "  "; " — the ways players join several artists.
    [GeneratedRegex(@"\s*(?:,|;|&|\s+(?:и|x|×|feat\.?|ft\.?|featuring|при\s+уч\.?)\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    /// <summary>
    /// "KARADA, сложно найти" → "KARADA". Players often send every artist in one string,
    /// while lyrics databases file the song under the first one.
    /// </summary>
    public static string Primary(string artist)
    {
        var first = Separator().Split(artist.Trim(), 2)[0].Trim();
        return first.Length > 0 ? first : artist.Trim();
    }
}
