using ExcelSearch___CB.Data;
using ExcelSearch___CB.Models;
using ExcelSearch___CB.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;
        private readonly FileIndexingService _indexing;
        private readonly IConfiguration _config;
        private readonly ConfigurationService _configService;

        public AdminController(IWebHostEnvironment env, AppDbContext db,
            UserManager<AppUser> userManager, FileIndexingService indexing,
            IConfiguration config, ConfigurationService configService)
        { _env = env; _db = db; _userManager = userManager; _indexing = indexing; _config = config; _configService = configService; }

        private string UploadsPath => Path.Combine(_env.WebRootPath, "Uploads");
        private string DataFolder => _config.GetValue("Data:Folder",
            Path.Combine(_env.WebRootPath, "Uploads"));
        private string ExportsFolder => Path.Combine(_env.WebRootPath, "Exports");

        private async Task<(string Name, string Role)> GetCurrentUserInfo()
        {
            var user = await _userManager.GetUserAsync(User);
            var roles = await _userManager.GetRolesAsync(user!);
            return (user?.FullName ?? User.Identity?.Name
                ?? _config.GetValue("SeedUsers:Admin:FullName", "Administrator"),
                roles.FirstOrDefault()
                ?? _config.GetValue("RoleNames:Admin", "Admin")!);
        }

        public IActionResult Index() => RedirectToAction("Overview");

        // ── Overview ─────────────────────────────────────────────────

        public async Task<IActionResult> Overview()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking().ToListAsync();
            var totalRecords = await _db.IndexedRecords.AsNoTracking().CountAsync();
            var failed = files.Count(f => f.Status == "Failed");
            var indexed = files.Count(f => f.Status == "Indexed");
            var totalBytes = files.Sum(f => f.FileSize);
            var storageLimit = _config.GetValue<double>("Storage:LimitGB", 50);

            var config = await _configService.GetAppConfig();
            var overviewStrings = await _configService.GetStringsByPage("Overview");

            ViewBag.AppConfig = config;
            ViewBag.UIStrings = overviewStrings;

            return View(new OverviewViewModel
            {
                AdminName = name, AdminRole = role,
                FilesIndexed = indexed,
                FilesIndexedDescription = files.Count + " files uploaded",
                TotalRecords = totalRecords,
                TotalRecordsDescription = "Across all sheets",
                FailedFiles = failed,
                FailedFilesDescription = failed == 0 ? "No failed files" : failed + " need attention",
                StorageUsed = (decimal)Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                StorageLimit = (decimal)storageLimit,
                LastIndexTime = files.Any() ? files.Max(f => f.LastIndexedAt ?? f.UploadedAt) : DateTime.Now,
                PipelineStages = new List<PipelineStageViewModel>
                {
                    new() { StageName="Upload", Details=files.Count+" files", Status=files.Count>0?"Ready":"Waiting" },
                    new() { StageName="Validate", Details=indexed+" passed", Status=indexed>0?"Ready":"Waiting" },
                    new() { StageName="Store", Details="Database", Status="Ready" },
                    new() { StageName="Index", Details=totalRecords+" records", Status=totalRecords>0?"Complete":"Pending" },
                    new() { StageName="Ready", Details=indexed+"/"+files.Count, Status=indexed==files.Count&&files.Count>0?"Complete":"Pending" }
                }
            });
        }

        // ── Upload (single + batch + folder scan) ────────────────────

        public async Task<IActionResult> Upload()
        {
            var (name, role) = await GetCurrentUserInfo();
            var recent = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.UploadedAt).Take(10).ToListAsync();

            ViewBag.DataFolder = DataFolder;
            var model = new UploadViewModel
            {
                AdminName = name, AdminRole = role,
                RecentUploads = recent.Select(f => new UploadedFileViewModel
                {
                    FileName = f.FileName, FileSize = FormatSize(f.FileSize),
                    RowCount = f.RowCount, UploadedTime = FormatTimeAgo(f.UploadedAt), Status = f.Status
                }).ToList()
            };
            return View(model);
        }

        [HttpPost]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> StartIndexing(List<IFormFile> excelFiles, string duplicateAction = "skip")
        {
            if (excelFiles == null || excelFiles.Count == 0)
            {
                TempData["Message"] = "Please select at least one file.";
                return RedirectToAction("Upload");
            }

            int success = 0, failed = 0, skipped = 0, replaced = 0;
            foreach (var file in excelFiles)
            {
                if (file.Length == 0) continue;
                string ext = Path.GetExtension(file.FileName).ToLower();
                if (ext != ".xlsx" && ext != ".xls" && ext != ".csv")
                { failed++; continue; }

                if (!Directory.Exists(UploadsPath)) Directory.CreateDirectory(UploadsPath);
                string path = Path.Combine(UploadsPath, Path.GetFileName(file.FileName));

                // Check duplicate before saving
                bool isDuplicate = System.IO.File.Exists(path);
                if (isDuplicate)
                {
                    var dup = _indexing.CheckDuplicate(path, file.FileName);
                    if (dup.Exists && dup.SameHash && (duplicateAction == "skip" || duplicateAction != "replace"))
                    { skipped++; continue; }
                    if (dup.Exists && duplicateAction == "keepboth")
                    {
                        var newName = Path.GetFileNameWithoutExtension(file.FileName) + "_"
                            + DateTime.Now.ToString("yyyyMMddHHmmss") + ext;
                        path = Path.Combine(UploadsPath, newName);
                    }
                }

                try
                {
                    // Save the file to disk first
                    await using (var stream = new FileStream(path, FileMode.Create,
                        FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    { await file.CopyToAsync(stream); }

                    // Index the new file
                    _indexing.IndexFile(path, file.FileName);

                    if (isDuplicate) replaced++;
                    else success++;
                }
                catch (Exception ex)
                {
                    // Indexing failed. The file is on disk but not indexed.
                    // The old index (if any) was already deleted by IndexFile before parsing.
                    // Mark as failed in DB for retry.
                    try
                    {
                        _db.IndexedFiles.Add(new IndexedFile
                        {
                            FileName = file.FileName,
                            FilePath = path,
                            FileSize = file.Length,
                            FileHash = FileIndexingService.ComputeFileHash(path),
                            Status = "Failed",
                            ErrorMessage = ex.Message,
                            UploadedAt = DateTime.Now,
                            RowCount = 0, WorksheetCount = 0, Worksheets = ""
                        });
                        _db.SaveChanges();
                    }
                    catch { }
                    failed++;
                }
            }

            TempData["Message"] = $"Complete: {success} indexed, {replaced} replaced, {skipped} skipped, {failed} failed.";
            return RedirectToAction("IndexMonitor");
        }

        // ── Duplicate check endpoint (returns JSON) ──────────────────

        [HttpPost]
        public IActionResult CheckDuplicate(string fileName)
        {
            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path))
                return Json(new { exists = false });

            var dup = _indexing.CheckDuplicate(path, fileName);
            return Json(new
            {
                exists = dup.Exists,
                sameHash = dup.SameHash,
                incomingHash = dup.IncomingHash,
                existingHash = dup.ExistingHash,
                incomingSize = dup.IncomingSize,
                existingSize = dup.ExistingSize,
                incomingName = dup.IncomingName,
                existingName = dup.ExistingName,
                existingFile = dup.ExistingFile
            });
        }

        [HttpPost]
        public IActionResult ScanFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return Json(new { error = "No folder path provided." });

            if (!Directory.Exists(folderPath))
                return Json(new { error = "Folder does not exist: " + folderPath });

            try
            {
                var files = _indexing.ScanFolderForNewFiles(folderPath);
                int indexed = 0, skipped = 0, failed = 0;

                foreach (var file in files)
                {
                    try
                    {
                        if (_indexing.IsAlreadyIndexed(file)) { skipped++; continue; }
                        _indexing.IndexFile(file, Path.GetFileName(file), folderPath);
                        indexed++;
                    }
                    catch { failed++; }
                }

                return Json(new { found = files.Count, indexed, skipped, failed });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        // ── Folder scan ──────────────────────────────────────────────

        public async Task<IActionResult> IndexMonitor()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking().ToListAsync();
            var c = files.Count(f => f.Status == "Indexed");
            var f = files.Count(f => f.Status == "Failed");
            var p = files.Count(f => f.Status == "Indexing");
            var tr = files.Sum(x => x.RowCount);

            var model = new IndexMonitorViewModel
            {
                AdminName = name, AdminRole = role,
                QueueFiles = files.Count, ProcessingFiles = p,
                CompletedFiles = c, FailedFiles = f,
                CurrentFile = files.FirstOrDefault(x => x.Status == "Indexing")?.FileName
                    ?? (files.Any() ? files.Last().FileName : "No files"),
                CurrentWorksheet = "-", RowsProcessed = tr, TotalRows = tr,
                CompletionPercentage = files.Count > 0 ? (int)((double)c / files.Count * 100) : 0,
                EstimatedTime = c == files.Count ? "All complete" : p > 0 ? "Processing..." : "~" + (files.Count - c) + " remaining",
                QueueItems = files.Select(x => new IndexQueueItemViewModel
                { FileName = x.FileName, Status = x.Status }).ToList(),
                Activities = files.OrderByDescending(x => x.UploadedAt).Take(10).Select(x =>
                    new IndexActivityViewModel
                    {
                        Message = x.Status == "Indexed" ? "Indexing completed"
                            : x.Status == "Failed" ? x.ErrorMessage ?? "Indexing failed"
                            : x.Status == "Indexing" ? "Indexing in progress" : "File uploaded",
                        Time = FormatTimeAgo(x.UploadedAt)
                    }).ToList()
            };
            return View(model);
        }

        // ── Re-index all ─────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReindexAll()
        {
            var files = await _db.IndexedFiles.AsNoTracking().ToListAsync();
            int done = 0, failed = 0;

            foreach (var f in files)
            {
                if (!System.IO.File.Exists(f.FilePath)) { failed++; continue; }
                try { _indexing.IndexFile(f.FilePath, f.FileName, f.SourceFolder); done++; }
                catch { failed++; }
            }

            TempData["Message"] = $"Re-index complete: {done} succeeded, {failed} failed.";
            return RedirectToAction("IndexMonitor");
        }

        // ── All Files ────────────────────────────────────────────────

        public async Task<IActionResult> AllFiles()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.UploadedAt).ToListAsync();

            return View(new AllFilesViewModel
            {
                AdminName = name, AdminRole = role,
                TotalFiles = files.Count, SummaryText = files.Count + " files tracked",
                Files = files.Select(f => new FileItemViewModel
                {
                    FileName = f.FileName, FileSize = FormatSize(f.FileSize),
                    LastIndexed = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy, HH:mm"),
                    Records = f.RowCount, Status = f.Status
                }).ToList()
            });
        }

        // ── Delete file (with log + double confirm) ──────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteFile(string fileName, string confirm)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return RedirectToAction("AllFiles");
            if (confirm != "DELETE") { TempData["Message"] = "Deletion cancelled."; return RedirectToAction("AllFiles"); }

            var file = await _db.IndexedFiles.FirstOrDefaultAsync(f => f.FileName == fileName);
            if (file == null) { TempData["Message"] = "File not found."; return RedirectToAction("AllFiles"); }

            int recordCount = await _db.IndexedRecords
                .CountAsync(r => r.IndexedFileId == file.Id);

            // Delete records
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", file.Id);
            _db.IndexedFiles.Remove(file);

            // Log deletion
            var user = await _userManager.GetUserAsync(User);
            _db.DeletionLogs.Add(new DeletionLog
            {
                DeletedByUserId = user?.Id ?? "",
                DeletedByUserName = user?.FullName ?? User.Identity?.Name ?? "Admin",
                FileName = file.FileName, FilePath = file.FilePath,
                FileSize = file.FileSize, RecordCount = recordCount,
                Reason = "Manual deletion", DeletedAt = DateTime.Now
            });
            await _db.SaveChangesAsync();

            string physical = Path.Combine(UploadsPath, fileName);
            if (System.IO.File.Exists(physical)) System.IO.File.Delete(physical);

            TempData["Message"] = $"File '{fileName}' deleted ({recordCount:N0} records removed).";
            return RedirectToAction("AllFiles");
        }

        [HttpPost][ValidateAntiForgeryToken]
        public async Task<IActionResult> ReindexFile(string fileName)
        {
            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path))
            { TempData["Message"] = "File not found."; return RedirectToAction("AllFiles"); }
            try { _indexing.IndexFile(path, fileName); TempData["Message"] = "Re-indexed."; }
            catch (Exception ex) { TempData["Message"] = "Failed: " + ex.Message; }
            return RedirectToAction("AllFiles");
        }

        [HttpPost][ValidateAntiForgeryToken]
        public Task<IActionResult> RetryFile(string fileName) => ReindexFile(fileName);

        public IActionResult ViewFile(string fileName)
        {
            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path)) { TempData["Message"] = "File not found."; return RedirectToAction("AllFiles"); }
            return RedirectToAction("Preview", "UserDashboard", new { path });
        }

        // ── Failed Files ─────────────────────────────────────────────

        public async Task<IActionResult> FailedFiles()
        {
            var (name, role) = await GetCurrentUserInfo();
            var failed = await _db.IndexedFiles.AsNoTracking()
                .Where(f => f.Status == "Failed").OrderByDescending(f => f.UploadedAt).ToListAsync();
            return View(new FailedFilesViewModel
            {
                AdminName = name, AdminRole = role, TotalFailedFiles = failed.Count,
                Description = failed.Count > 0 ? "Files that could not be indexed." : "No issues.",
                Files = failed.Select(f => new FailedFileItemViewModel
                {
                    FileName = f.FileName, Reason = f.ErrorMessage ?? "Unknown",
                    FileSize = FormatSize(f.FileSize),
                    LastAttempt = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy, HH:mm")
                }).ToList()
            });
        }

        // ── Search History ───────────────────────────────────────────

        public async Task<IActionResult> SearchHistory()
        {
            var (name, role) = await GetCurrentUserInfo();
            var logs = await _db.SearchLogs.AsNoTracking()
                .OrderByDescending(l => l.SearchTime).Take(500).ToListAsync();
            var today = DateTime.Today;

            return View(new SearchHistoryViewModel
            {
                AdminName = name, AdminRole = role,
                TotalSearches = await _db.SearchLogs.AsNoTracking().CountAsync(),
                TodaySearches = await _db.SearchLogs.AsNoTracking().CountAsync(l => l.SearchTime.Date == today),
                AverageResults = await _db.SearchLogs.AsNoTracking().AnyAsync()
                    ? (int)await _db.SearchLogs.AsNoTracking().AverageAsync(l => (double)l.ResultCount) : 0,
                MostPopularSearch = await _db.SearchLogs.AsNoTracking()
                    .GroupBy(l => l.SearchTerm).OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefaultAsync() ?? "-",
                Searches = logs.Select(l => new SearchHistoryItemViewModel
                {
                    UserName = l.UserName ?? "User", SearchTerm = l.SearchTerm,
                    SearchMode = l.SearchMode, Results = l.ResultCount,
                    SearchTime = l.SearchTime.ToString("dd MMM yyyy HH:mm")
                }).ToList()
            });
        }

        // ── Indexing History ────────────────────────────────────────────

        public async Task<IActionResult> IndexingHistory()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.UploadedAt).Take(200).ToListAsync();

            return View(new IndexingHistoryViewModel
            {
                AdminName = name,
                AdminRole = role,
                TotalRuns = files.Count,
                SuccessfulRuns = files.Count(f => f.Status == "Indexed"),
                FailedRuns = files.Count(f => f.Status == "Failed"),
                TotalRowsIndexed = files.Where(f => f.Status == "Indexed").Sum(f => f.RowCount),
                Runs = files.Select(f => new IndexingRunViewModel
                {
                    FileName = f.FileName,
                    FileSize = FormatSize(f.FileSize),
                    RowCount = f.RowCount,
                    Status = f.Status,
                    ErrorMessage = f.ErrorMessage ?? "",
                    StartedAt = f.UploadedAt.ToString("dd MMM yyyy HH:mm"),
                    CompletedAt = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy HH:mm"),
                    Duration = ((f.LastIndexedAt ?? f.UploadedAt) - f.UploadedAt).ToString(@"hh\:mm\:ss")
                }).ToList()
            });
        }

        // ── Export History ───────────────────────────────────────────

        public async Task<IActionResult> ExportHistory()
        {
            var (name, role) = await GetCurrentUserInfo();
            var logs = await _db.ExportLogs.AsNoTracking()
                .OrderByDescending(l => l.ExportTime).Take(500).ToListAsync();
            long totalBytes = logs.Where(l => System.IO.File.Exists(l.ExportPath))
                .Sum(l => new FileInfo(l.ExportPath).Length);

            return View(new ExportHistoryViewModel
            {
                AdminName = name, AdminRole = role,
                TotalExports = await _db.ExportLogs.AsNoTracking().CountAsync(),
                TodayExports = await _db.ExportLogs.AsNoTracking().CountAsync(l => l.ExportTime.Date == DateTime.Today),
                WeeklyExports = await _db.ExportLogs.AsNoTracking().CountAsync(l => l.ExportTime >= DateTime.Today.AddDays(-7)),
                TotalSize = FormatSize(totalBytes),
                Exports = logs.Select(l => new ExportHistoryItemViewModel
                {
                    FileName = l.FileName, ExportedBy = l.UserName ?? "User",
                    Rows = l.RowCount, ExportTime = l.ExportTime.ToString("dd MMM yyyy HH:mm"), Status = l.Status
                }).ToList()
            });
        }

        // ── Clear Exports ────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearExports(string confirm)
        {
            if (confirm != "CLEAR") { TempData["Message"] = "Clear cancelled."; return RedirectToAction("ExportHistory"); }

            int count = 0;
            if (Directory.Exists(ExportsFolder))
            {
                foreach (var f in Directory.GetFiles(ExportsFolder))
                { System.IO.File.Delete(f); count++; }
            }
            _db.ExportLogs.RemoveRange(_db.ExportLogs);
            await _db.SaveChangesAsync();

            TempData["Message"] = $"{count} export files cleared.";
            return RedirectToAction("ExportHistory");
        }

        // ── Storage ──────────────────────────────────────────────────

        public async Task<IActionResult> Storage()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking().Where(f => f.Status == "Indexed").ToListAsync();
            long totalBytes = files.Sum(f => f.FileSize);
            double limit = _config.GetValue<double>("Storage:LimitGB", 50);
            double used = totalBytes / (1024.0 * 1024.0 * 1024.0);
            int pct = limit > 0 ? (int)(used / limit * 100) : 0;
            if (pct > 100) pct = 100;

            var uploadFiles = new List<StorageFileViewModel>();
            if (Directory.Exists(UploadsPath))
                foreach (var f in Directory.GetFiles(UploadsPath).Take(10))
                { var fi = new FileInfo(f); uploadFiles.Add(new StorageFileViewModel { FileName = fi.Name, FileSize = FormatSize(fi.Length) }); }

            var dbPath = Path.Combine(_env.ContentRootPath, "App_Data", "ExcelSearch.db");
            var dbSize = System.IO.File.Exists(dbPath) ? FormatSize(new FileInfo(dbPath).Length) : "Unknown";

            return View(new StorageViewModel
            {
                AdminName = name, AdminRole = role,
                StorageUsed = Math.Round(used, 2) + " GB", StorageLimit = limit + " GB",
                StoragePercentage = pct, TotalFiles = files.Count,
                DatabaseSize = dbSize,
                TotalRecords = (await _db.IndexedRecords.AsNoTracking().CountAsync()).ToString("N0"),
                LastBackup = _config.GetValue("Storage:BackupPath", "").Length > 0
                    ? System.IO.File.Exists(_config.GetValue<string>("Storage:BackupPath"))
                        ? new FileInfo(_config.GetValue<string>("Storage:BackupPath")!).LastWriteTime.ToString("dd MMM yyyy HH:mm")
                        : "No backup found"
                    : "Not Configured",
                Files = uploadFiles
            });
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static string FormatSize(long b) => b switch
        {
            > 1_073_741_824 => Math.Round(b / 1_073_741_824.0, 2) + " GB",
            > 1_048_576 => Math.Round(b / 1_048_576.0, 2) + " MB",
            > 1_024 => Math.Round(b / 1_024.0, 2) + " KB",
            > 0 => b + " bytes", _ => "0 KB"
        };

        private static string FormatTimeAgo(DateTime dt)
        {
            var s = DateTime.Now - dt;
            if (s.TotalMinutes < 1) return "Just now";
            if (s.TotalMinutes < 60) return (int)s.TotalMinutes + " min ago";
            if (s.TotalHours < 24) return (int)s.TotalHours + " hr" + (s.TotalHours >= 2 ? "s" : "") + " ago";
            if (s.TotalDays < 7) return (int)s.TotalDays + " day" + (s.TotalDays >= 2 ? "s" : "") + " ago";
            return dt.ToString("dd MMM yyyy");
        }
    }
}
