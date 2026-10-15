using System.Text;

namespace Comet.Core;

/// <summary>
/// Reads and writes the tab delimited history format documented in the original
/// README, so files written by the Windows build load here unchanged:
///
///   COMMAND[TAB][TAB][TAB][TAB]DESCRIPTION
///
/// Runs of tabs collapse to one separator. A line with only a command uses the
/// command as its own description. A blank line toggles the button shade, which
/// is how the command files in the COMMANDS folder group related commands.
///
/// A **script line opens and closes a script button; the lines between are its
/// body, kept verbatim and run by <see cref="ScriptRunner"/> through
/// <see cref="Script.FromLines"/>.
/// </summary>
public static class HistoryFile
{
    private const string ScriptMarker = "**script";

    public static IReadOnlyList<CommandEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<CommandEntry>();
        int buttonStyle = 0;
        bool insideScript = false;
        var scriptBody = new List<string>();
        CommandEntry? scriptEntry = null;

        foreach (string rawLine in lines)
        {
            string[] parsed = rawLine.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parsed.Length == 0)
            {
                // Blank line: flip the shade for the next group.
                buttonStyle = (buttonStyle + 1) % 2;
                continue;
            }

            string command = parsed[0].Trim();
            if (command.Length == 0) continue;

            if (command.StartsWith(ScriptMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (!insideScript)
                {
                    insideScript = true;
                    scriptBody.Clear();
                    string label = parsed.Length > 1 && parsed[1].Length > 0 ? parsed[1] : "Script";
                    scriptEntry = new CommandEntry(label, label, 2, CommandEntryType.ScriptRunner);
                }
                else
                {
                    insideScript = false;
                    if (scriptEntry != null)
                    {
                        if (parsed.Length > 1 && parsed[1].Length > 0) scriptEntry.Description = parsed[1];
                        scriptEntry.ScriptLines = scriptBody.ToArray();
                        entries.Add(scriptEntry);
                        scriptEntry = null;
                    }
                }
                continue;
            }

            if (insideScript)
            {
                scriptBody.Add(rawLine);
                continue;
            }

            string description = parsed.Length > 1 && parsed[1].Length > 0 ? parsed[1] : command;
            entries.Add(new CommandEntry(command, description, buttonStyle));
        }

        // A script block left unterminated still becomes a button rather than vanishing.
        if (scriptEntry != null)
        {
            scriptEntry.ScriptLines = scriptBody.ToArray();
            entries.Add(scriptEntry);
        }

        return entries;
    }

    public static IReadOnlyList<CommandEntry> Load(string path) => Parse(File.ReadAllLines(path));

    /// <summary>
    /// Serializes back to the same format the Windows build writes, so a file
    /// saved here still opens there.
    /// </summary>
    public static string[] Serialize(IEnumerable<CommandEntry> entries)
    {
        var lines = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.EntryType == CommandEntryType.ScriptRunner)
            {
                lines.Add(ScriptMarker + "\t\t\t\t" + entry.Command);
                if (entry.ScriptLines != null) lines.AddRange(entry.ScriptLines);
                lines.Add(ScriptMarker + "\t\t\t\t" + entry.Description);
            }
            else
            {
                lines.Add(entry.Command + "\t\t\t\t" + entry.Description);
            }
        }
        return lines.ToArray();
    }

    public static void Save(string path, IEnumerable<CommandEntry> entries)
        => File.WriteAllLines(path, Serialize(entries), Encoding.UTF8);
}
