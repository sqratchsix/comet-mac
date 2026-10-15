using System.Text;

namespace Comet.Core;

/// <summary>
/// Puts a timestamp at the start of every terminal line, sent and received alike,
/// so the whole output sits in one column behind the stamps.
///
/// The Windows build stamped each message once, which left the second and later
/// lines of a multi-line reply hard against the margin. This tracks whether the
/// terminal is mid-line across calls, so a reply split over several reads gets
/// one stamp per line and never a stamp in the middle of one.
///
/// CR LF, CR CR LF and a lone CR each count as one line break, because devices
/// mix them freely (Quectel echoes "ATI\r\r\n"). Breaks are written as "\n".
/// </summary>
public sealed class TerminalLineStamper
{
    private bool pendingCr;

    /// <summary>True when the next character written starts a new line.</summary>
    public bool AtLineStart { get; private set; } = true;

    public void Reset()
    {
        AtLineStart = true;
        pendingCr = false;
    }

    /// <summary>
    /// Splits text into segments, inserting a stamp segment before each line that
    /// has content. A null stamp only tracks line state and normalises nothing.
    /// </summary>
    public IReadOnlyList<(string Text, bool IsStamp)> Process(string text, string? stamp)
    {
        var segments = new List<(string, bool)>();
        if (string.IsNullOrEmpty(text)) return segments;

        if (stamp == null)
        {
            Track(text);
            segments.Add((text, false));
            return segments;
        }

        var line = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\n' && pendingCr)
            {
                // Second half of a CR LF split across two reads, or the LF after CR CR.
                pendingCr = false;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                // A run of CRs before an LF is one break, not several.
                while (c == '\r' && i + 1 < text.Length && text[i + 1] == '\r') i++;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                else pendingCr = c == '\r';

                Flush(line, segments, stamp);
                segments.Add(("\n", false));
                AtLineStart = true;
                continue;
            }

            pendingCr = false;
            line.Append(c);
        }

        Flush(line, segments, stamp);
        return segments;
    }

    private void Flush(StringBuilder line, List<(string, bool)> segments, string stamp)
    {
        if (line.Length == 0) return;
        if (AtLineStart) segments.Add((stamp + " ", true));
        segments.Add((line.ToString(), false));
        line.Clear();
        AtLineStart = false;
    }

    private void Track(string text)
    {
        char last = text[^1];
        AtLineStart = last == '\n' || last == '\r';
        pendingCr = last == '\r';
    }
}
