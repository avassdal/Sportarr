using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sportarr.Api.Services;

namespace Sportarr.Api.Endpoints;

public static class SystemBackupEndpoints
{
    public static IEndpointRouteBuilder MapSystemBackupEndpoints(this IEndpointRouteBuilder app)
    {
        // Surface the actual error to the UI instead of a generic
        // 500. Without this the frontend shows "Failed to fetch
        // backups" with no clue whether the cause is a missing
        // folder, a permission issue on /config/Backups, or a
        // bogus BackupFolder set in config.xml.
        app.MapGet("/api/system/backup", async (BackupService backupService, ILogger<BackupService> logger) =>
        {
            try
            {
                var backups = await backupService.GetBackupsAsync();
                return Results.Ok(backups);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogError(ex, "[Backup] Permission denied reading backup folder");
                return Results.Problem(
                    title: "Backup folder not accessible",
                    detail: $"Sportarr can't read or write the backup folder. Check that the path is writable by the container user (PUID/PGID). Underlying error: {ex.Message}",
                    statusCode: 500);
            }
            catch (DirectoryNotFoundException ex)
            {
                logger.LogError(ex, "[Backup] Backup folder path is invalid");
                return Results.Problem(
                    title: "Backup folder path invalid",
                    detail: $"The configured backup folder doesn't exist and the parent directory is missing. Set a valid absolute path under Settings -> General -> Backup, or leave it empty to use the default. Underlying error: {ex.Message}",
                    statusCode: 500);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Backup] Unexpected error listing backups");
                return Results.Problem(
                    title: "Failed to list backups",
                    detail: ex.Message,
                    statusCode: 500);
            }
        });

        app.MapPost("/api/system/backup", async (BackupService backupService, ILogger<BackupService> logger, string? note) =>
        {
            try
            {
                var backup = await backupService.CreateBackupAsync(note);
                return Results.Ok(backup);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogError(ex, "[Backup] Permission denied creating backup");
                return Results.Problem(
                    title: "Backup folder not writable",
                    detail: $"Sportarr can't write to the backup folder. Check that the path is writable by the container user (PUID/PGID). Underlying error: {ex.Message}",
                    statusCode: 500);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "[Backup] IO error creating backup");
                return Results.Problem(
                    title: "Backup write failed",
                    detail: $"Disk I/O error while writing the backup zip. Most commonly: the backup folder is full, on a read-only mount, or the database file is locked by another process. Underlying error: {ex.Message}",
                    statusCode: 500);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Backup] Unexpected error creating backup");
                return Results.Problem(
                    title: "Failed to create backup",
                    detail: ex.Message,
                    statusCode: 500);
            }
        });

        app.MapPost("/api/system/backup/restore/{backupName}", async (string backupName, BackupService backupService) =>
        {
            try
            {
                await backupService.RestoreBackupAsync(backupName);
                return Results.Ok(new { message = "Backup restored successfully. Please restart Sportarr for changes to take effect." });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/system/backup/download/{backupName}", async (string backupName, BackupService backupService) =>
        {
            try
            {
                var backups = await backupService.GetBackupsAsync();
                var backup = backups.FirstOrDefault(b => b.Name == backupName);
                if (backup == null || !File.Exists(backup.Path))
                    return Results.NotFound(new { message = "Backup file not found" });

                return Results.File(backup.Path, "application/zip", backupName);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapDelete("/api/system/backup/{backupName}", async (string backupName, BackupService backupService) =>
        {
            try
            {
                await backupService.DeleteBackupAsync(backupName);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/system/backup/cleanup", async (BackupService backupService) =>
        {
            try
            {
                await backupService.CleanupOldBackupsAsync();
                return Results.Ok(new { message = "Old backups cleaned up successfully" });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        return app;
    }
}
