using System.Runtime.InteropServices;

namespace Sportarr.Api.Helpers;

/// <summary>
/// Pre-add validation for root folder paths. Mirrors the platform-specific
/// guards the upstream Servarr family applies — system paths, recycle
/// bins, mapped network drives, non-writable mounts — and adds a
/// touch-test so a misconfigured Docker volume that's mounted read-only
/// is caught immediately rather than during the first failed import.
/// </summary>
public static class RootFolderValidator
{
    public record Result(bool IsValid, string? Reason);

    public static Result Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new Result(false, "Path is required.");

        if (!Directory.Exists(path))
            return new Result(false, $"Path does not exist on disk: {path}");

        if (IsSystemPath(path))
            return new Result(false, $"Path is a system folder and is not allowed as a root folder: {path}");

        if (IsRecycleOrMetaFolder(path))
            return new Result(false, $"Path is a recycle bin or filesystem metadata folder: {path}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsMappedNetworkDrive(path))
            return new Result(false, $"Mapped network drives can't be used as root folders. Use a UNC path (\\\\server\\share) instead: {path}");

        if (!IsWritable(path, out var writeError))
            return new Result(false, $"Path is not writable: {writeError}");

        return new Result(true, null);
    }

    private static bool IsSystemPath(string path)
    {
        var normalized = NormalizeForCompare(path);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Reject the OS / Program Files trees and the bare drive root.
            // Drive roots themselves are dangerous because the user can
            // accidentally have everything on C:\ get scanned/moved.
            string[] windowsBlocked = {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            };
            foreach (var blocked in windowsBlocked)
            {
                if (string.IsNullOrEmpty(blocked)) continue;
                var n = NormalizeForCompare(blocked);
                if (normalized.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Bare drive root like "C:\".
            try
            {
                var pathRoot = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(pathRoot) &&
                    string.Equals(NormalizeForCompare(pathRoot), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore
            }

            return false;
        }

        // Linux / macOS: reject the obvious system roots and "/" itself.
        // "/data" or "/mnt/whatever" is fine; "/" or "/etc" is not.
        if (normalized == "/" || string.IsNullOrEmpty(normalized))
            return true;

        string[] unixBlockedPrefixes = {
            "/proc", "/sys", "/dev", "/run", "/boot", "/etc",
            "/usr", "/var", "/sbin", "/bin", "/lib", "/lib64",
            "/root",
        };
        foreach (var prefix in unixBlockedPrefixes)
        {
            if (normalized == prefix ||
                normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsRecycleOrMetaFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (string.IsNullOrEmpty(name)) return false;
        return name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lost+found", StringComparison.Ordinal)
            || name.Equals(".Trash", StringComparison.Ordinal)
            || name.Equals(".Trashes", StringComparison.Ordinal);
    }

    private static bool IsMappedNetworkDrive(string path)
    {
        try
        {
            var pathRoot = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(pathRoot) || pathRoot.StartsWith(@"\\"))
                return false;
            var drive = new DriveInfo(pathRoot);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWritable(string path, out string? error)
    {
        error = null;
        // Drop a tiny probe file and immediately remove it. The probe name
        // is unique-per-attempt so concurrent validations never collide;
        // we delete it in a finally so a failure halfway through doesn't
        // leave litter behind.
        var probe = Path.Combine(path, $".sportarr-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Permission denied. Check ownership and mode of {path}. ({ex.Message})";
            return false;
        }
        catch (IOException ex)
        {
            error = $"I/O error writing to {path}: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Unexpected error writing to {path}: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* ignore */ }
        }
    }

    private static string NormalizeForCompare(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar);
    }
}
