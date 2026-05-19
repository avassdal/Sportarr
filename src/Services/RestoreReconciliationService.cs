using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

/// <summary>
/// Orchestrates the post-restore reconciliation flow so the admin doesn't
/// have to do anything after restoring a backup beyond "click Restore."
///
/// Steps the orchestrator runs, in order:
///   1. Open a RestoreReport row in `pending` so the UI can render
///      "reconciliation in progress" as soon as the admin clicks restore
///   2. Trigger DiskScanService (which already does the actual file
///      existence checking + MissingSince timestamping)
///   3. Wait for the scan to settle (poll on EventFile.LastVerified) then
///      count Found / Missing / SkippedUnreachableRoot
///   4. Attempt a PathRemapService.DetectAsync; if it produced a
///      confident suggestion, record it in the report but do NOT auto-
///      apply (the admin reviews + confirms in the UI)
///   5. Flip the report to `completed`
///
/// This service does not move files, write paths, or change events
/// itself; it composes the existing services that already do those
/// things. The value it adds is "after restore, you get one record that
/// tells you what happened" instead of having to dig through logs.
/// </summary>
public class RestoreReconciliationService
{
    private readonly SportarrDbContext _db;
    private readonly DiskScanService _diskScan;
    private readonly PathRemapService _pathRemap;
    private readonly ILogger<RestoreReconciliationService> _logger;

    // How long the orchestrator waits for the disk scan to complete before
    // it gives up and reports the partial state. The scan is bounded by
    // the number of EventFile rows and IO speed; for very large libraries
    // (50k+ events) the full sweep takes a few minutes. 10 minutes is the
    // safety ceiling so the report doesn't sit in `pending` forever if
    // something goes wrong upstream.
    private static readonly TimeSpan ScanWaitCeiling = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(5);

    public RestoreReconciliationService(
        SportarrDbContext db,
        DiskScanService diskScan,
        PathRemapService pathRemap,
        ILogger<RestoreReconciliationService> logger)
    {
        _db = db;
        _diskScan = diskScan;
        _pathRemap = pathRemap;
        _logger = logger;
    }

    /// <summary>
    /// Create a pending RestoreReport row and return its id. The caller
    /// is expected to spawn a background task that calls RunAsync with
    /// the returned id so the actual scan + polling doesn't block the
    /// HTTP request that triggered the restore. The pending row gives
    /// the UI something to navigate to immediately.
    /// </summary>
    public async Task<int> BeginAsync(
        string backupFileName,
        BackupManifest? manifest,
        CancellationToken ct = default)
    {
        var report = new RestoreReport
        {
            BackupFileName = backupFileName,
            StartedAt = DateTime.UtcNow,
            Status = "pending",
            SourceHost = manifest?.SourceHost,
            SourceSportarrVersion = manifest?.SportarrVersion,
            ManifestJson = manifest != null
                ? JsonSerializer.Serialize(manifest)
                : null,
        };
        _db.RestoreReports.Add(report);
        await _db.SaveChangesAsync(ct);
        return report.Id;
    }

    /// <summary>
    /// Run the reconciliation work for the report row created by
    /// BeginAsync. Long-running: triggers the disk scan, waits for it
    /// to settle, fills in counts, attempts a path-remap detection,
    /// flips the report status to completed (or failed on exception).
    /// Idempotent on the report row -- safe to invoke once per restore.
    /// </summary>
    public async Task RunAsync(int reportId, CancellationToken ct = default)
    {
        var report = await _db.RestoreReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report == null)
        {
            _logger.LogWarning("[RestoreReconciliation] Report {Id} not found", reportId);
            return;
        }

        try
        {
            // Capture the baseline LastVerified time so we can detect when
            // the disk scanner has visited every row at least once after
            // we triggered it. If a row's LastVerified is still earlier
            // than baseline after the wait window, the scan didn't finish;
            // we report a partial result rather than blocking forever.
            var baseline = DateTime.UtcNow;
            _diskScan.TriggerScanNow();

            var deadline = baseline + ScanWaitCeiling;
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(ScanPollInterval, ct);
                var oldest = await _db.EventFiles
                    .AsNoTracking()
                    .Where(ef => ef.FilePath != null)
                    .Select(ef => (DateTime?)ef.LastVerified)
                    .DefaultIfEmpty(DateTime.UtcNow)
                    .MinAsync(ct);
                if (oldest.HasValue && oldest.Value >= baseline) break;
            }

            // Pull final counts from the (now-reconciled) database. We do
            // this even on partial scan completion so the admin sees the
            // best information available.
            report.TotalEventFiles = await _db.EventFiles.CountAsync(ct);
            report.FilesFound = await _db.EventFiles
                .CountAsync(ef => ef.Exists, ct);
            report.FilesMissing = await _db.EventFiles
                .CountAsync(ef => !ef.Exists && ef.MissingSince != null, ct);
            // Files under unreachable roots are still flagged Exists=true
            // by the disk scan (it skips them rather than mark missing),
            // so subtract them from the Found total to derive the count.
            // The exact count is reachable via the same root-folder check
            // DiskScanService used; we redo it here to keep this service
            // independent.
            report.FilesSkippedUnreachableRoot = await CountUnreachableAsync(ct);
            if (report.FilesSkippedUnreachableRoot > 0)
            {
                report.FilesFound = Math.Max(0,
                    report.FilesFound - report.FilesSkippedUnreachableRoot);
            }

            // Attempt to detect a path-prefix drift. If the scan found
            // missing files AND they share a common prefix that resolves
            // under one of the configured root folders, the report
            // captures the suggestion. Apply is still gated on the admin.
            try
            {
                var remap = await _pathRemap.DetectAsync(ct);
                if (remap.HasSuggestion)
                {
                    var suggestion = new
                    {
                        from = remap.OldPrefix,
                        to = remap.NewPrefix,
                        affected = remap.AffectedRowCount,
                        sampleMatches = remap.SampleMatches,
                        sampleSize = remap.SampleSize,
                        notes = remap.Notes,
                    };
                    report.PathRemapsJson = JsonSerializer.Serialize(new[] { suggestion });
                }
                else if (!string.IsNullOrWhiteSpace(remap.Notes))
                {
                    report.Notes = AppendNote(report.Notes, remap.Notes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RestoreReconciliation] PathRemap detection failed; continuing");
                report.Notes = AppendNote(report.Notes,
                    $"Path remap detection failed: {ex.Message}");
            }

            report.CompletedAt = DateTime.UtcNow;
            report.Status = "completed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RestoreReconciliation] Reconciliation failed");
            report.Status = "failed";
            report.CompletedAt = DateTime.UtcNow;
            report.Notes = AppendNote(report.Notes, $"Failed: {ex.Message}");
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<int> CountUnreachableAsync(CancellationToken ct)
    {
        var settings = await _db.MediaManagementSettings.FirstOrDefaultAsync(ct);
        var unreachable = (settings?.RootFolders ?? new List<RootFolder>())
            .Where(rf => !string.IsNullOrEmpty(rf.Path) && !Directory.Exists(rf.Path))
            .Select(rf => rf.Path
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar)
            .ToList();
        if (unreachable.Count == 0) return 0;

        var allPaths = await _db.EventFiles
            .AsNoTracking()
            .Where(ef => ef.FilePath != null)
            .Select(ef => ef.FilePath!)
            .ToListAsync(ct);

        var skipped = 0;
        foreach (var p in allPaths)
        {
            foreach (var prefix in unreachable)
            {
                if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    break;
                }
            }
        }
        return skipped;
    }

    private static string? AppendNote(string? existing, string add)
    {
        if (string.IsNullOrWhiteSpace(add)) return existing;
        return string.IsNullOrWhiteSpace(existing) ? add : existing + "\n" + add;
    }
}
