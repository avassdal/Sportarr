using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text.Json;

namespace Sportarr.Api.Services;

/// <summary>
/// Handles database backup and restore operations
/// </summary>
public class BackupService
{
    private readonly SportarrDbContext _db;
    private readonly ILogger<BackupService> _logger;
    private readonly string _dataDirectory;
    private readonly string _databasePath;
    private readonly ConfigService _configService;

    public BackupService(SportarrDbContext db, ILogger<BackupService> logger, IConfiguration configuration, ConfigService configService)
    {
        _db = db;
        _logger = logger;
        _configService = configService;
        _dataDirectory = configuration["Sportarr:DataPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        _databasePath = Path.Combine(_dataDirectory, "sportarr.db");
    }

    /// <summary>
    /// Get backup folder path from settings
    /// </summary>
    private async Task<string> GetBackupFolderAsync()
    {
        var config = await _configService.GetConfigAsync();
        var backupFolder = config.BackupFolder;

        if (string.IsNullOrWhiteSpace(backupFolder))
        {
            backupFolder = Path.Combine(_dataDirectory, "Backups");
        }

        if (!Directory.Exists(backupFolder))
        {
            Directory.CreateDirectory(backupFolder);
        }

        return backupFolder;
    }

    /// <summary>
    /// List all available backups
    /// </summary>
    public async Task<List<BackupInfo>> GetBackupsAsync()
    {
        var backupFolder = await GetBackupFolderAsync();
        var backups = new List<BackupInfo>();

        if (!Directory.Exists(backupFolder))
        {
            return backups;
        }

        foreach (var file in Directory.GetFiles(backupFolder, "sportarr_backup_*.zip"))
        {
            var fileInfo = new FileInfo(file);
            backups.Add(new BackupInfo
            {
                Name = Path.GetFileName(file),
                Path = file,
                Size = fileInfo.Length,
                CreatedAt = fileInfo.CreationTimeUtc
            });
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>
    /// Create a new backup of the database
    /// </summary>
    public async Task<BackupInfo> CreateBackupAsync(string? note = null)
    {
        var backupFolder = await GetBackupFolderAsync();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"sportarr_backup_{timestamp}.zip";
        var backupPath = Path.Combine(backupFolder, backupFileName);

        _logger.LogInformation("Creating backup: {BackupPath}", backupPath);

        try
        {
            // Ensure WAL mode checkpoint to get a consistent backup
            await _db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL)");

            // Create backup zip file
            using (var zipArchive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                // Add main database file
                if (File.Exists(_databasePath))
                {
                    zipArchive.CreateEntryFromFile(_databasePath, "sportarr.db");
                }

                // Add WAL file if it exists
                var walPath = _databasePath + "-wal";
                if (File.Exists(walPath))
                {
                    zipArchive.CreateEntryFromFile(walPath, "sportarr.db-wal");
                }

                // Add SHM file if it exists
                var shmPath = _databasePath + "-shm";
                if (File.Exists(shmPath))
                {
                    zipArchive.CreateEntryFromFile(shmPath, "sportarr.db-shm");
                }

                // Add config.xml (API key, auth, bind URL)
                var configPath = Path.Combine(_dataDirectory, "config.xml");
                if (File.Exists(configPath))
                {
                    zipArchive.CreateEntryFromFile(configPath, "config.xml");
                }

                // Add backup metadata
                var metadata = zipArchive.CreateEntry("backup_metadata.txt");
                using (var writer = new StreamWriter(metadata.Open()))
                {
                    writer.WriteLine($"Backup Created: {DateTime.UtcNow:O}");
                    writer.WriteLine($"Sportarr Version: {Version.AppVersion}");
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        writer.WriteLine($"Note: {note}");
                    }
                }

                // Structured manifest.json that the restore-preview screen
                // can read to answer "what am I about to commit to?"
                // without inspecting the .db. Mostly counts + a sample of
                // EventFile paths so the path-remap heuristic has enough
                // signal to detect drift before any data lands.
                await WriteManifestAsync(zipArchive, note);
            }

            var fileInfo = new FileInfo(backupPath);
            _logger.LogInformation("Backup created successfully: {BackupPath} ({Size} bytes)", backupPath, fileInfo.Length);

            return new BackupInfo
            {
                Name = backupFileName,
                Path = backupPath,
                Size = fileInfo.Length,
                CreatedAt = fileInfo.CreationTimeUtc
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup: {BackupPath}", backupPath);
            throw new InvalidOperationException($"Failed to create backup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Restore database from a backup. Optionally accepts a `scope` set
    /// that restricts which artifacts are pulled back from the zip:
    ///   * "db" -- the SQLite database files (default; also implicit when
    ///     scope is empty / null since the original behaviour always
    ///     restored the db)
    ///   * "config" -- config.xml
    /// Per-section restore lets the admin recover a piece of state (e.g.
    /// quality profiles, indexers, IPTV config -- everything lives in the
    /// db, so "db" recovers everything at once) without overwriting the
    /// rest. Returns the parsed BackupManifest if one was present in the
    /// zip so the caller can hand it to RestoreReconciliationService.
    /// </summary>
    public async Task<BackupManifest?> RestoreBackupAsync(
        string backupName,
        IReadOnlySet<string>? scope = null)
    {
        var backupFolder = await GetBackupFolderAsync();
        var backupPath = Path.Combine(backupFolder, backupName);

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup file not found: {backupName}");
        }

        // Default scope = everything, matching the pre-scope behaviour.
        var effectiveScope = scope != null && scope.Count > 0
            ? scope
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "db", "config" };

        _logger.LogInformation(
            "Restoring backup: {BackupPath} (scope: {Scope})",
            backupPath, string.Join(", ", effectiveScope));

        BackupManifest? manifest = null;
        try
        {
            // Close all database connections
            await _db.Database.CloseConnectionAsync();

            // Create a restore directory
            var restoreDir = Path.Combine(_dataDirectory, "restore_temp");
            if (Directory.Exists(restoreDir))
            {
                Directory.Delete(restoreDir, true);
            }
            Directory.CreateDirectory(restoreDir);

            // Extract backup
            ZipFile.ExtractToDirectory(backupPath, restoreDir);

            // Parse manifest.json if the backup zip carries one. Older
            // backups omit this file and we proceed with manifest = null;
            // the reconciliation orchestrator falls back to a blind scan
            // in that case.
            var manifestPath = Path.Combine(restoreDir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Backup carries a manifest.json but it failed to parse; proceeding without it");
                }
            }

            if (effectiveScope.Contains("db"))
            {
                // Backup current database before replacing (safety measure)
                var currentBackupPath = _databasePath + ".before_restore";
                if (File.Exists(_databasePath))
                {
                    File.Copy(_databasePath, currentBackupPath, true);
                }

                // Replace database files
                var restoredDbPath = Path.Combine(restoreDir, "sportarr.db");
                if (File.Exists(restoredDbPath))
                {
                    File.Copy(restoredDbPath, _databasePath, true);
                }

                var restoredWalPath = Path.Combine(restoreDir, "sportarr.db-wal");
                if (File.Exists(restoredWalPath))
                {
                    File.Copy(restoredWalPath, _databasePath + "-wal", true);
                }

                var restoredShmPath = Path.Combine(restoreDir, "sportarr.db-shm");
                if (File.Exists(restoredShmPath))
                {
                    File.Copy(restoredShmPath, _databasePath + "-shm", true);
                }
            }

            if (effectiveScope.Contains("config"))
            {
                var restoredConfigPath = Path.Combine(restoreDir, "config.xml");
                if (File.Exists(restoredConfigPath))
                {
                    var configPath = Path.Combine(_dataDirectory, "config.xml");
                    File.Copy(restoredConfigPath, configPath, true);
                    _logger.LogInformation("Restored config.xml from backup");
                }
            }

            // Clean up restore directory
            Directory.Delete(restoreDir, true);

            _logger.LogInformation("Backup restored successfully: {BackupPath}", backupPath);
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup: {BackupPath}", backupPath);
            throw new InvalidOperationException($"Failed to restore backup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Read just the manifest.json out of a backup zip without applying
    /// the restore. Used by the restore-preview endpoint to power the
    /// "here's what you're about to commit to" screen.
    /// </summary>
    public async Task<BackupManifest?> ReadManifestAsync(string backupName)
    {
        var backupFolder = await GetBackupFolderAsync();
        var backupPath = Path.Combine(backupFolder, backupName);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException($"Backup file not found: {backupName}");

        using var archive = ZipFile.OpenRead(backupPath);
        var entry = archive.GetEntry("manifest.json");
        if (entry == null) return null;

        using var reader = new StreamReader(entry.Open());
        var json = await reader.ReadToEndAsync();
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Manifest.json present but failed to parse for {BackupName}", backupName);
            return null;
        }
    }

    /// <summary>
    /// Accept an uploaded backup zip from the admin UI and stash it in
    /// the backups folder so the existing list / restore flow picks it up.
    /// The file name is sanitized to a single basename so a malicious
    /// archive name can't traverse out of the backups folder.
    /// </summary>
    public async Task<BackupInfo> SaveUploadedBackupAsync(
        Stream uploadStream,
        string suggestedFileName)
    {
        var backupFolder = await GetBackupFolderAsync();
        // Sanitize: take only the basename, force .zip, avoid collision.
        var baseName = Path.GetFileNameWithoutExtension(
            Path.GetFileName(suggestedFileName) ?? "uploaded_backup");
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "uploaded_backup";
        var fileName = $"{baseName}.zip";
        var targetPath = Path.Combine(backupFolder, fileName);
        // If the target exists, append a timestamp suffix.
        if (File.Exists(targetPath))
        {
            fileName = $"{baseName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
            targetPath = Path.Combine(backupFolder, fileName);
        }

        await using (var fs = File.Create(targetPath))
        {
            await uploadStream.CopyToAsync(fs);
        }

        var fileInfo = new FileInfo(targetPath);
        _logger.LogInformation(
            "Uploaded backup saved: {Path} ({Size} bytes)",
            targetPath, fileInfo.Length);

        return new BackupInfo
        {
            Name = fileName,
            Path = targetPath,
            Size = fileInfo.Length,
            CreatedAt = fileInfo.CreationTimeUtc,
        };
    }

    /// <summary>
    /// Generate the structured manifest.json that goes into every new
    /// backup zip. The manifest captures counts and a representative path
    /// sample so the restore-preview screen can answer "what am I about
    /// to restore?" without having to inspect the .db inside the zip.
    /// </summary>
    private async Task WriteManifestAsync(ZipArchive zipArchive, string? note)
    {
        try
        {
            var manifest = new BackupManifest
            {
                CreatedAt = DateTime.UtcNow,
                SportarrVersion = Version.AppVersion,
                SourceHost = Environment.MachineName,
                Note = note,
                TotalEvents = await _db.Events.CountAsync(),
                TotalEventFiles = await _db.EventFiles.CountAsync(),
                TotalLeagues = await _db.Leagues.CountAsync(),
            };

            var settings = await _db.MediaManagementSettings.FirstOrDefaultAsync();
            if (settings?.RootFolders != null)
            {
                manifest.RootFolders = settings.RootFolders
                    .Where(rf => !string.IsNullOrEmpty(rf.Path))
                    .Select(rf => rf.Path)
                    .ToList();
            }

            manifest.SampleFiles = await _db.EventFiles
                .AsNoTracking()
                .Where(ef => ef.FilePath != null)
                .OrderBy(ef => ef.Id)
                .Take(200)
                .Select(ef => ef.FilePath!)
                .ToListAsync();

            var entry = zipArchive.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(entry.Open());
            var json = JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true });
            await writer.WriteAsync(json);
        }
        catch (Exception ex)
        {
            // Manifest is purely informational; failing to write it must
            // not break the backup itself. Restore falls back to a blind
            // reconciliation when the manifest is missing.
            _logger.LogWarning(ex,
                "Failed to write backup manifest.json; backup will restore without preview metadata");
        }
    }

    /// <summary>
    /// Delete a backup file
    /// </summary>
    public async Task DeleteBackupAsync(string backupName)
    {
        var backupFolder = await GetBackupFolderAsync();
        var backupPath = Path.Combine(backupFolder, backupName);

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup file not found: {backupName}");
        }

        _logger.LogInformation("Deleting backup: {BackupPath}", backupPath);
        File.Delete(backupPath);
    }

    /// <summary>
    /// Clean up old backups based on retention policy
    /// </summary>
    public async Task CleanupOldBackupsAsync()
    {
        var config = await _configService.GetConfigAsync();
        var retentionDays = config.BackupRetention; // Default 28 days

        var backups = await GetBackupsAsync();
        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

        foreach (var backup in backups.Where(b => b.CreatedAt < cutoffDate))
        {
            _logger.LogInformation("Cleaning up old backup: {BackupName} (created {CreatedAt})", backup.Name, backup.CreatedAt);
            await DeleteBackupAsync(backup.Name);
        }
    }
}

/// <summary>
/// Information about a backup file
/// </summary>
public class BackupInfo
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
    public string SizeFormatted => FormatBytes(Size);

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
