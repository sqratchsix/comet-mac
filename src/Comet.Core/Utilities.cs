using System.Text;

namespace Comet.Core;

/// <summary>
/// Ported from Comet1.Utilities. The file dialog helpers and the ToolStrip
/// extension method are gone: those were WinForms UI concerns and now live in
/// the Avalonia layer. What remains is pure conversion and file work.
/// </summary>
public static class Utilities
{
    public static string FindUniqueFilepath(string basePathName, string extension)
    {
        string path = "";
        for (int i = 1; i < 999; i++)
        {
            path = basePathName + i.ToString("000") + extension;
            if (!File.Exists(path)) break;
        }
        return path;
    }

    public static bool WriteStringToFile(string fileName, string data)
    {
        try
        {
            using var fs = new FileStream(fileName, FileMode.Append, FileAccess.Write);
            using var sw = new StreamWriter(fs);
            sw.WriteLine(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts between milliseconds and the named unit. ToMs true means the
    /// input is in the named unit and should come back as milliseconds.
    /// </summary>
    public static double ConvertMs(double input, string timeType, bool toMs)
    {
        return timeType switch
        {
            "milli" => input,
            "sec" => toMs ? input * 1000 : input / 1000,
            "min" => toMs ? input * 60 * 1000 : input / (60 * 1000),
            "hour" => toMs ? input * 60 * 60 * 1000 : input / (60 * 60 * 1000),
            _ => 0
        };
    }

    public static string ConvertHex(string hexString, bool removeWhitespace)
    {
        if (removeWhitespace)
        {
            hexString = hexString.Replace(" ", string.Empty)
                                 .Replace("\r\n", string.Empty)
                                 .Replace("\n", string.Empty);
        }

        try
        {
            var ascii = new StringBuilder();
            for (int i = 0; i + 1 < hexString.Length; i += 2)
            {
                string hs = hexString.Substring(i, 2);
                uint decval = Convert.ToUInt32(hs, 16);
                ascii.Append(Convert.ToChar(decval));
            }
            return ascii.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static string ConvertStringToHex(string inputString, bool removeWhitespace)
    {
        if (removeWhitespace) inputString = inputString.Replace(" ", string.Empty);

        var hexOutput = new StringBuilder();
        try
        {
            foreach (char c in inputString)
            {
                hexOutput.Append($"{Convert.ToInt32(c):X}");
            }
        }
        catch (Exception)
        {
            // Fall through with whatever was converted so far, as the original did.
        }
        return hexOutput.ToString();
    }
}

/// <summary>
/// Collects .txt files from files and directories dropped onto the history pane.
/// Ported from Comet1.RecursiveFileProcessor.
/// </summary>
public class RecursiveFileProcessor
{
    private readonly List<string> allFilePaths = new();
    private const string FileExtension = ".txt";

    public string[] GetAllPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path)) ProcessFile(path);
            else if (Directory.Exists(path)) ProcessDirectory(path);
        }
        return allFilePaths.ToArray();
    }

    public void ProcessDirectory(string targetDirectory)
    {
        try
        {
            foreach (string fileName in Directory.GetFiles(targetDirectory))
            {
                if (fileName.Contains(FileExtension, StringComparison.OrdinalIgnoreCase)) ProcessFile(fileName);
            }
            foreach (string subdirectory in Directory.GetDirectories(targetDirectory))
            {
                ProcessDirectory(subdirectory);
            }
        }
        catch (Exception)
        {
            // Unreadable directory, skip it.
        }
    }

    public void ProcessFile(string path) => allFilePaths.Add(path);
}
