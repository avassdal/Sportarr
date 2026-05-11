namespace Sportarr.Api.Helpers;

/// <summary>
/// Hardcoded extension lists used by the FailDownloads import-time
/// policy. Match the upstream lists so behavior stays predictable when
/// users move between -arr-family apps. Adding entries requires a
/// considered review since flipping any download containing these
/// extensions to Failed (with a blocklist entry) is a one-way trip
/// the user can't easily un-do.
/// </summary>
public static class RejectedFileExtensions
{
    /// <summary>
    /// Bare executables. A torrent with a .exe inside is a near-certain
    /// scam — legitimate sports content never bundles one.
    /// </summary>
    public static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat",
        ".cmd",
        ".exe",
        ".sh",
    };

    /// <summary>
    /// Extensions that aren't bare executables but are still fishy in a
    /// media download — Windows shortcuts, PowerShell scripts, screen-
    /// savers, less-common archive formats often used to obfuscate
    /// payload contents.
    /// </summary>
    public static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arj",
        ".lnk",
        ".lzh",
        ".ps1",
        ".scr",
        ".vbs",
        ".zipx",
    };

    /// <summary>
    /// Parse the user's free-form extensions string (".nfo, .url, txt")
    /// into a normalized HashSet that can be compared to file extensions
    /// returned by Path.GetExtension. Tolerates commas, whitespace, the
    /// optional leading dot, and case variation.
    /// </summary>
    public static HashSet<string> ParseUserList(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;

        foreach (var token in raw.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = token.Trim();
            if (ext.Length == 0) continue;
            if (!ext.StartsWith('.')) ext = "." + ext;
            set.Add(ext);
        }
        return set;
    }
}
