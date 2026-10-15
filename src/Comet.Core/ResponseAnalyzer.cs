using System.Text.RegularExpressions;

namespace Comet.Core;

/// <summary>
/// Reply checks used by the response_* and log script functions, ported from
/// Comet1.ResponseAnalyzer. Matching ignores case, as it did there. One change:
/// logged values keep the device's own capitalisation, where the original
/// lowercased everything it logged.
/// </summary>
public static class ResponseAnalyzer
{
    private static readonly Regex LineBreak = new(@"\r\n|\r|\n");

    public static IEnumerable<string> Lines(string buffer) => LineBreak.Split(buffer);

    /// <summary>response_str: true once the reply contains the expected text.</summary>
    public static bool Contains(string buffer, string expected) =>
        buffer.Contains(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// response_log and log: the text after <paramref name="prefix"/> on the first
    /// line that has it, or with no prefix the first line with anything on it.
    /// </summary>
    public static bool TryGetValueAfter(string buffer, string prefix, out string value)
    {
        value = "";
        foreach (var line in Lines(buffer))
        {
            if (prefix.Length == 0)
            {
                if (line.Trim().Length == 0) continue;
                value = line;
                return true;
            }

            int at = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            value = line.Remove(at, prefix.Length);
            return true;
        }
        return false;
    }

    /// <summary>
    /// response_int_between: finds the whole number after <paramref name="prefix"/>.
    /// Returns false until it can be read; then <paramref name="passed"/> says
    /// whether it lies strictly between the bounds, as in the original.
    /// </summary>
    public static bool TryCheckBetween(string buffer, string prefix, int low, int high,
        out bool passed, out string detail)
    {
        passed = false;
        detail = $"\"{prefix}\" not found";
        if (!TryGetValueAfter(buffer, prefix, out string rest)) return false;

        // Lines only count once complete, so "+CSQ: 2" is not mistaken for 21.
        var match = Regex.Match(rest, @"^\s*(-?\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value))
        {
            detail = $"No number after \"{prefix}\"";
            return false;
        }

        passed = low < value && value < high;
        detail = $"Measured {value}, expected between {low} and {high}";
        return true;
    }

    /// <summary>The buffer with any final, unterminated line removed.</summary>
    public static string CompleteLines(string buffer)
    {
        int end = Math.Max(buffer.LastIndexOf('\n'), buffer.LastIndexOf('\r'));
        return end < 0 ? "" : buffer.Substring(0, end + 1);
    }
}
