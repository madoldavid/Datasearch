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
using ClosedXML.Excel;
using Newtonsoft.Json;
using System.Globalization;
using System.Text;
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

                    // Queue the new file for background indexing
                    _indexing.QueueFileForIndexing(path, file.FileName);

                    if (isDuplicate) replaced++;
                    else success++;
                }
                catch (Exception ex)
                {
                    // Upload failed. The file could not be queued.
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
                        _indexing.QueueFileForIndexing(file, Path.GetFileName(file), folderPath);
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
                try { _indexing.QueueFileForIndexing(f.FilePath, f.FileName, f.SourceFolder); done++; }
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
            try { _indexing.QueueFileForIndexing(path, fileName); TempData["Message"] = "Queued for re-indexing."; }
            catch (Exception ex) { TempData["Message"] = "Failed: " + ex.Message; }
            return RedirectToAction("AllFiles");
        }

        [HttpPost][ValidateAntiForgeryToken]
        public Task<IActionResult> RetryFile(string fileName) => ReindexFile(fileName);

        public IActionResult ViewFile(string fileName)
        {
            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path)) { TempData["Message"] = "File not found."; return RedirectToAction("AllFiles"); }
            return RedirectToAction("Preview", "Admin", new { path });
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

            var largestFiles = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.FileSize)
                .Take(10)
                .ToListAsync();

            var uploadFiles = largestFiles.Select(f => new StorageFileViewModel 
            { 
                FileName = f.FileName, 
                FileSize = FormatSize(f.FileSize),
                Status = f.Status 
            }).ToList();

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

        private bool IsPathSafe(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var allowed = new[] {
                Path.Combine(_env.WebRootPath, "Uploads"),
                Path.Combine(_env.WebRootPath, "Exports"),
                _env.ContentRootPath
            };
            var resolved = Path.GetFullPath(filePath);
            return allowed.Any(a =>
                resolved.StartsWith(Path.GetFullPath(a), StringComparison.OrdinalIgnoreCase));
        }

        // ── Admin Search & Export ─────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Search(string searchText)
        {
            var (name, role) = await GetCurrentUserInfo();
            ViewBag.SearchText = searchText;
            ViewBag.Columns = await GetColumns();
            ViewBag.FilesIndexed = await _db.IndexedFiles.AsNoTracking().CountAsync(f => f.Status == "Indexed");
            var uid = await GetUserId();
            ViewBag.RecentSearches = await _db.SearchLogs.AsNoTracking().Where(l => l.UserId == uid).OrderByDescending(l => l.SearchTime).Select(l => new { Term = l.SearchTerm, Mode = l.SearchMode, Column = l.SearchColumn ?? "All" }).Distinct().Take(5).ToListAsync();
            return View(new AdminBaseViewModel { AdminName = name, AdminRole = role });
        }

        [HttpPost]
        public async Task<IActionResult> Search(string searchText, string searchColumn, string searchMode = "Partial")
        {
            var (name, role) = await GetCurrentUserInfo();
            if (string.IsNullOrWhiteSpace(searchText))
            { TempData["SearchError"] = "Please enter something to search."; return RedirectToAction("Search"); }

            var model = await ExecuteSearch(searchText, searchColumn, searchMode, null);
            if (model.Files.Count == 0)
            { TempData["SearchError"] = "No matching records found."; return RedirectToAction("Search"); }

            await SaveSnapshot(model);
            _db.SearchLogs.Add(new SearchLog { UserId = await GetUserId(), UserName = User.Identity?.Name ?? "Admin", SearchTerm = searchText, SearchMode = searchMode, SearchColumn = searchColumn ?? "All", ResultCount = model.TotalMatches, SearchTime = DateTime.Now });
            await _db.SaveChangesAsync();

            ViewBag.AdminName = name;
            ViewBag.AdminRole = role;
            return View("SearchResults", model);
        }

        [HttpPost]
        public async Task<IActionResult> FilterSearch(string conditions, string logic = "AND")
        {
            var (name, role) = await GetCurrentUserInfo();
            if (string.IsNullOrWhiteSpace(conditions))
            { TempData["SearchError"] = "No filter conditions."; return RedirectToAction("Search"); }

            var filterGroups = JsonConvert.DeserializeObject<List<List<FilterCondition>>>(conditions);
            if (filterGroups == null || filterGroups.Count == 0)
            { TempData["SearchError"] = "No filter conditions."; return RedirectToAction("Search"); }

            var skippedSheets = new List<string>();
            HashSet<(int FileId, int RowNum)> allResults = null;

            foreach (var andGroup in filterGroups)
            {
                HashSet<(int FileId, int RowNum)> groupResults = null;
                foreach (var filter in andGroup)
                {
                    var (rowSet, fieldExists) = await EvaluateFilter(filter);
                    if (!fieldExists) skippedSheets.Add(filter.Field);
                    if (rowSet != null)
                    {
                        if (groupResults == null) groupResults = rowSet;
                        else groupResults.UnionWith(rowSet);
                    }
                }
                if (groupResults != null)
                {
                    if (allResults == null) allResults = groupResults;
                    else allResults.IntersectWith(groupResults);
                }
                if (allResults != null && allResults.Count == 0) break;
            }

            var model = new SearchResultViewModel
            {
                SearchTerm = string.Join(" AND ",
                    filterGroups.Select(g => "(" + string.Join(" OR ",
                        g.Select(f => $"{f.Field} {f.Operator} {f.Value}")) + ")")),
                SkippedSheets = skippedSheets.Distinct().ToList()
            };

            if (allResults != null && allResults.Count > 0)
            {
                var fIds = allResults.Select(r => r.FileId).Distinct().ToList();
                var rNums = allResults.Select(r => r.RowNum).Distinct().ToList();
                var matches = await _db.IndexedRecords.AsNoTracking().Include(r => r.IndexedFile).Where(r => fIds.Contains(r.IndexedFileId) && rNums.Contains(r.RowNumber)).ToListAsync();
                matches = matches.Where(r => allResults.Contains((r.IndexedFileId, r.RowNumber))).ToList();
                BuildResultModel(model, matches);
            }

            if (model.Files.Count == 0)
            { TempData["SearchError"] = "No matching records found."; return RedirectToAction("Search"); }

            await SaveSnapshot(model);
            _db.SearchLogs.Add(new SearchLog { UserId = await GetUserId(), UserName = User.Identity?.Name ?? "Admin", SearchTerm = model.SearchTerm, SearchMode = "Filter", SearchColumn = "", ResultCount = model.TotalMatches, SearchTime = DateTime.Now });
            await _db.SaveChangesAsync();

            ViewBag.AdminName = name;
            ViewBag.AdminRole = role;
            return View("SearchResults", model);
        }

        [HttpGet]
        public async Task<IActionResult> FilterBuilder()
        {
            var (name, role) = await GetCurrentUserInfo();
            ViewBag.Columns = await GetColumns();
            return View(new AdminBaseViewModel { AdminName = name, AdminRole = role });
        }

        [HttpGet]
        public async Task<IActionResult> SearchResults(int page = 1)
        {
            var (name, role) = await GetCurrentUserInfo();
            var model = await LoadSnapshot();
            if (model == null)
            { TempData["SearchError"] = "No search results. Perform a search first."; return RedirectToAction("Search"); }
            model.CurrentPage = page; model.PageSize = 25;
            model.TotalPages = Math.Max(1, (int)Math.Ceiling((double)model.Files.Count / 25));
            model.Files = model.Files.Skip((page - 1) * 25).Take(25).ToList();

            ViewBag.AdminName = name;
            ViewBag.AdminRole = role;
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Preview(string path, bool matchingOnly = true)
        {
            try
            {
                var (name, role) = await GetCurrentUserInfo();
                if (string.IsNullOrWhiteSpace(path) || !IsPathSafe(path) || !System.IO.File.Exists(path))
                { TempData["SearchError"] = "File not found or access denied."; return RedirectToAction("Search"); }

                ViewBag.FilePath = path;
                ViewBag.FileName = Path.GetFileName(path);
                ViewBag.MatchingOnly = matchingOnly;
                ViewBag.AdminName = name;
                ViewBag.AdminRole = role;

                if (!matchingOnly)
                {
                    var (allRows, _) = _indexing.PreviewFile(path, 500);
                    return View(allRows);
                }

                var model = await LoadSnapshot();
                if (model != null)
                {
                    var fileResult = model.Files.FirstOrDefault(f => f.FilePath == path);
                    if (fileResult != null)
                    {
                        ViewBag.MatchedColumn = fileResult.SampleValue != null ? "Sample: " + fileResult.SampleValue : null;
                        ViewBag.MatchCount = fileResult.Rows.Count;

                        var displayRows = new List<Dictionary<string, string>>();
                        int displayRowNum = 1;
                        foreach (var row in fileResult.Rows)
                        {
                            var displayRow = new Dictionary<string, string>(row);
                            displayRow["__source_row__"] = displayRowNum.ToString();
                            displayRows.Add(displayRow);
                            displayRowNum++;
                        }
                        return View(displayRows);
                    }
                }

                var (fallbackRows, _) = _indexing.PreviewFile(path, 500);
                return View(fallbackRows);
            }
            catch
            { TempData["SearchError"] = "Unable to preview."; return RedirectToAction("Search"); }
        }

        [HttpGet]
        public async Task<IActionResult> ExportCurrentFile(string path, string format = "xlsx",
            string grouping = "flat", bool metadataColumns = false, string selectedRows = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !IsPathSafe(path) || !System.IO.File.Exists(path))
                { TempData["SearchError"] = "Export file not found or access denied."; return RedirectToAction("Search"); }

                var searchModel = await LoadSnapshot();
                string exportFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportFolder)) Directory.CreateDirectory(exportFolder);

                HashSet<int> selectedIndices = null;
                if (!string.IsNullOrWhiteSpace(selectedRows))
                    selectedIndices = new HashSet<int>(
                        selectedRows.Split(',').Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i >= 0));

                var uid = await GetUserId();
                return format == "csv"
                    ? await ExportCsv(path, exportFolder, searchModel, grouping, metadataColumns, selectedIndices, uid)
                    : await ExportXlsx(path, exportFolder, searchModel, grouping, metadataColumns, selectedIndices, uid);
            }
            catch (Exception ex)
            { TempData["SearchError"] = "Export failed: " + ex.Message; return RedirectToAction("Search"); }
        }

        private async Task<string> GetUserId()
        {
            var u = await _userManager.GetUserAsync(User);
            return u?.Id ?? "";
        }

        private async Task<SearchResultViewModel> ExecuteSearch(
            string text, string column, string mode, List<FilterCondition> filters)
        {
            var m = new SearchResultViewModel { SearchTerm = text };
            IQueryable<IndexedRecord> q = _db.IndexedRecords.AsNoTracking().Include(r => r.IndexedFile);
            if (!string.IsNullOrWhiteSpace(column) && column != "All")
                q = q.Where(r => r.ColumnName == column);

            List<IndexedRecord> matches;
            if (mode == "Exact")
                matches = await q.Where(r => r.ColumnValue.ToLower() == text.ToLower()).OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber).ToListAsync();
            else if (mode == "Flexible")
            {
                var stripped = text.Replace(" ", "").Replace("-", "").Replace("_", "").ToLower();
                var like = "%" + string.Join("%", stripped.ToCharArray()) + "%";
                matches = await q.Where(r => EF.Functions.Like(
                    r.ColumnValue.Replace(" ", "").Replace("-", "").Replace("_", "").ToLower(), like))
                    .OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber).ToListAsync();
            }
            else
                matches = await q.Where(r => r.ColumnValue.ToLower().Contains(text.ToLower())).OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber).ToListAsync();

            BuildResultModel(m, matches);
            return m;
        }

        private async Task<(HashSet<(int, int)> matchingRows, bool fieldExists)> EvaluateFilter(
            FilterCondition filter)
        {
            IQueryable<IndexedRecord> q = _db.IndexedRecords.AsNoTracking()
                .Where(r => r.ColumnName == filter.Field);
            string val = filter.Value?.ToLower() ?? "";

            q = filter.Operator switch
            {
                "Equals" => q.Where(r => r.ColumnValue.ToLower() == val),
                "Does not equal" => q.Where(r => r.ColumnValue.ToLower() != val),
                "Contains" => q.Where(r => r.ColumnValue.ToLower().Contains(val)),
                "Does not contain" => q.Where(r => !r.ColumnValue.ToLower().Contains(val)),
                "Starts with" => q.Where(r => r.ColumnValue.ToLower().StartsWith(val)),
                "Ends with" => q.Where(r => r.ColumnValue.ToLower().EndsWith(val)),
                "Greater than" => NumericWhere(q, val, ">"),
                "Greater than or equal" => NumericWhere(q, val, ">="),
                "Less than" => NumericWhere(q, val, "<"),
                "Less than or equal" => NumericWhere(q, val, "<="),
                "Between" => BetweenWhere(q, val),
                "Is empty" => q.Where(r => string.IsNullOrWhiteSpace(r.ColumnValue)),
                "Is not empty" => q.Where(r => !string.IsNullOrWhiteSpace(r.ColumnValue)),
                _ => q.Where(r => r.ColumnValue.ToLower().Contains(val))
            };

            int count = await q.CountAsync();
            bool fieldExists = count > 0 || await _db.IndexedRecords.AsNoTracking()
                .AnyAsync(r => r.ColumnName == filter.Field);

            if (count == 0) return (null, fieldExists);

            var rows = await q.Select(r => new { r.IndexedFileId, r.RowNumber })
                .Distinct().ToListAsync();
            return (new HashSet<(int, int)>(rows.Select(r => (r.IndexedFileId, r.RowNumber))), true);
        }

        private IQueryable<IndexedRecord> NumericWhere(IQueryable<IndexedRecord> q, string val, string op)
        {
            if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double _))
            {
                var candidates = q.Where(r => r.ColumnValue.TrimStart().Length > 0).Select(r => r.ColumnValue).Distinct().ToList();
                var paddedVal = val.TrimStart().PadLeft(20, '0');
                var matchingValues = candidates.Where(v =>
                {
                    var trimmed = v.TrimStart();
                    if (trimmed.Length == 0) return false;
                    if (!trimmed.All(c => char.IsDigit(c) || c == '.' || c == '-' || c == ',')) return false;
                    var padded = trimmed.PadLeft(20, '0');
                    return op switch
                    {
                        ">" => padded.CompareTo(paddedVal) > 0,
                        ">=" => padded.CompareTo(paddedVal) >= 0,
                        "<" => padded.CompareTo(paddedVal) < 0,
                        "<=" => padded.CompareTo(paddedVal) <= 0,
                        _ => false
                    };
                }).ToList();
                if (matchingValues.Count == 0) return q.Where(_ => false);
                return q.Where(r => matchingValues.Contains(r.ColumnValue));
            }
            return op switch
            {
                ">" => q.Where(r => r.ColumnValue.CompareTo(val) > 0),
                ">=" => q.Where(r => r.ColumnValue.CompareTo(val) >= 0),
                "<" => q.Where(r => r.ColumnValue.CompareTo(val) < 0),
                "<=" => q.Where(r => r.ColumnValue.CompareTo(val) <= 0),
                _ => q
            };
        }

        private IQueryable<IndexedRecord> BetweenWhere(IQueryable<IndexedRecord> q, string val)
        {
            var parts = val.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length < 2) parts = val.Split(new[] { " to " }, StringSplitOptions.None);
            if (parts.Length < 2) parts = val.Split(new[] { "," }, StringSplitOptions.None);
            if (parts.Length < 2) return q.Take(0);

            var lo = parts[0].Trim();
            var hi = parts[1].Trim();

            if (DateTime.TryParse(lo, out DateTime loDate) && DateTime.TryParse(hi, out DateTime hiDate))
            {
                var loStr = loDate.ToString("yyyyMMdd");
                var hiStr = hiDate.ToString("yyyyMMdd");
                var matches = q.Select(r => r.ColumnValue).Distinct().ToList().Where(v =>
                {
                    if (DateTime.TryParse(v, out DateTime cellDate))
                    {
                        var cellStr = cellDate.ToString("yyyyMMdd");
                        return string.Compare(cellStr, loStr, StringComparison.Ordinal) >= 0 && string.Compare(cellStr, hiStr, StringComparison.Ordinal) <= 0;
                    }
                    return false;
                }).ToList();
                if (matches.Count == 0) return q.Where(_ => false);
                return q.Where(r => matches.Contains(r.ColumnValue));
            }

            if (double.TryParse(lo, out double _) && double.TryParse(hi, out double _))
            {
                var paddedLo = lo.PadLeft(20, '0');
                var paddedHi = hi.PadLeft(20, '0');
                var matches = q.Select(r => r.ColumnValue).Distinct().ToList().Where(v =>
                {
                    var trimmed = v.TrimStart();
                    if (trimmed.Length == 0) return false;
                    if (!trimmed.All(c => char.IsDigit(c) || c == '.' || c == '-' || c == ',')) return false;
                    var padded = trimmed.PadLeft(20, '0');
                    return padded.CompareTo(paddedLo) >= 0 && padded.CompareTo(paddedHi) <= 0;
                }).ToList();
                if (matches.Count == 0) return q.Where(_ => false);
                return q.Where(r => matches.Contains(r.ColumnValue));
            }

            return q.Where(r => r.ColumnValue.CompareTo(lo) >= 0 && r.ColumnValue.CompareTo(hi) <= 0);
        }

        private async Task<IActionResult> ExportXlsx(string path, string folder,
            SearchResultViewModel model, string grouping, bool meta, HashSet<int> selected, string userId)
        {
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Path.GetFileNameWithoutExtension(path) + ".xlsx";
            string dest = Path.Combine(folder, name);
            int totalRows = 0;

            await Task.Run(() =>
            {
                using var wb = new XLWorkbook();
                var fileResult = model?.Files.FirstOrDefault(f => f.FilePath == path);
                if (fileResult != null && fileResult.Rows.Any())
                {
                    if (grouping == "byfile" && model != null)
                    {
                        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var fr in model.Files)
                        {
                            if (fr.Rows.Count == 0) continue;
                            string wsName = SafeSheetName(Path.GetFileNameWithoutExtension(fr.FileName));
                            if (!seenNames.Add(wsName)) wsName = SafeSheetName(wsName + "_" + seenNames.Count);
                            var ws = wb.Worksheets.Add(wsName);
                            WriteRowsToSheet(ws, fr.Rows, meta, fr.FileName, fr.Worksheet, selected, ref totalRows);
                        }
                    }
                    else if (grouping == "byworksheet")
                    {
                        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var fr in (model?.Files ?? new List<SearchFileResult> { fileResult }))
                        {
                            if (fr.Rows.Count == 0) continue;
                            string wsName = SafeSheetName(fr.Worksheet ?? "Sheet");
                            if (!seenNames.Add(wsName)) wsName = SafeSheetName(wsName + "_" + seenNames.Count);
                            var ws = wb.Worksheets.Add(wsName);
                            WriteRowsToSheet(ws, fr.Rows, meta, fr.FileName, fr.Worksheet, selected, ref totalRows);
                        }
                    }
                    else if (grouping == "bysearchtext")
                    {
                        string wsName = SafeSheetName(model?.SearchTerm ?? "Search");
                        var ws = wb.Worksheets.Add(wsName);
                        foreach (var fr in model?.Files ?? new List<SearchFileResult> { fileResult })
                            WriteRowsToSheet(ws, fr.Rows, meta, fr.FileName, fr.Worksheet, selected, ref totalRows, skipHeaderAfter: totalRows > 0);
                    }
                    else
                    {
                        var ws = wb.Worksheets.Add("Search Results");
                        WriteRowsToSheet(ws, fileResult.Rows, meta, fileResult.FileName, fileResult.Worksheet, selected, ref totalRows);
                    }
                }
                if (totalRows == 0)
                { var ws = wb.Worksheets.Add("Export"); ws.Cell(1, 1).Value = "Export from: " + Path.GetFileName(path); totalRows = 1; }
                wb.SaveAs(dest);
            });

            await LogExport(userId, name, dest, totalRows, grouping, model?.SearchTerm ?? "", selected != null);
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(dest);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }

        private async Task<IActionResult> ExportCsv(string path, string folder,
            SearchResultViewModel model, string grouping, bool meta, HashSet<int> selected, string userId)
        {
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Path.GetFileNameWithoutExtension(path) + ".csv";
            string dest = Path.Combine(folder, name);
            int totalRows = 0;

            await Task.Run(() =>
            {
                var fileResult = model?.Files.FirstOrDefault(f => f.FilePath == path);
                if (fileResult != null && fileResult.Rows.Any())
                {
                    var headers = fileResult.Rows.First().Keys.Where(k => k != "__source_row__").ToList();
                    if (meta) { headers.Insert(0, "Source Row"); headers.Insert(0, "Source Sheet"); headers.Insert(0, "Source File"); }
                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(",", headers));
                    int idx = 0;
                    foreach (var row in fileResult.Rows)
                    {
                        if (selected != null && !selected.Contains(idx)) { idx++; continue; }
                        idx++;
                        var vals = headers.Select(h =>
                        {
                            string v = "";
                            if (h == "Source File") v = fileResult.FileName;
                            else if (h == "Source Sheet") v = fileResult.Worksheet;
                            else if (h == "Source Row") v = row.ContainsKey("__source_row__") ? row["__source_row__"] : "";
                            else v = row.ContainsKey(h) ? row[h] : "";
                            return "\"" + v.Replace("\"", "\"\"") + "\"";
                        });
                        sb.AppendLine(string.Join(",", vals));
                        totalRows++;
                    }
                    System.IO.File.WriteAllText(dest, sb.ToString(), Encoding.UTF8);
                }
                else { System.IO.File.Copy(path, dest, true); totalRows = (int)new FileInfo(dest).Length; }
            });

            await LogExport(userId, name, dest, totalRows, grouping, model?.SearchTerm ?? "", selected != null);
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(dest);
            return File(bytes, "text/csv", name);
        }

        private static void WriteRowsToSheet(IXLWorksheet ws,
            List<Dictionary<string, string>> rows, bool meta,
            string sourceFile, string sourceSheet,
            HashSet<int> selected, ref int totalRows, bool skipHeaderAfter = false)
        {
            var allKeys = rows.FirstOrDefault()?.Keys.Where(k => k != "__source_row__").ToList() ?? new List<string>();
            var headers = new List<string>();
            if (!skipHeaderAfter)
            {
                if (meta) { headers.Add("Source File"); headers.Add("Source Sheet"); headers.Add("Source Row"); }
                headers.AddRange(allKeys);
                for (int c = 0; c < headers.Count; c++)
                    ws.Cell(1, c + 1).Value = headers[c];
            }

            int startRow = skipHeaderAfter ? ws.LastRowUsed()?.RowNumber() + 1 ?? 1 : 2;
            int idx = 0;
            foreach (var row in rows)
            {
                if (selected != null && !selected.Contains(idx)) { idx++; continue; }
                idx++;
                int col = 1;
                if (!skipHeaderAfter && meta)
                { ws.Cell(startRow, col++).Value = sourceFile; ws.Cell(startRow, col++).Value = sourceSheet; ws.Cell(startRow, col++).Value = row.ContainsKey("__source_row__") ? row["__source_row__"] : ""; }
                else if (skipHeaderAfter && meta)
                { ws.Cell(startRow, col++).Value = sourceFile; ws.Cell(startRow, col++).Value = sourceSheet; ws.Cell(startRow, col++).Value = row.ContainsKey("__source_row__") ? row["__source_row__"] : ""; }
                else col = 1;

                foreach (var k in allKeys)
                    ws.Cell(startRow, col++).Value = row.TryGetValue(k, out var v) ? v : "";
                startRow++;
                totalRows++;
            }
        }

        private static string SafeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet";
            var invalid = new[] { '\\', '/', '*', '?', ':', '[', ']' };
            var safe = new string(name.Where(c => !invalid.Contains(c)).ToArray());
            if (safe.Length > 31) safe = safe[..31];
            return string.IsNullOrWhiteSpace(safe) ? "Sheet" : safe;
        }

        private async Task LogExport(string userId, string fileName, string path,
            int rows, string grouping, string summary, bool selectedOnly)
        {
            _db.ExportLogs.Add(new ExportLog
            {
                UserId = userId, UserName = User.Identity?.Name ?? "Admin",
                FileName = fileName, ExportPath = path, RowCount = rows,
                GroupingMode = grouping, SearchSummary = summary,
                SelectedRowsOnly = selectedOnly, Status = "Completed", ExportTime = DateTime.Now
            });
            await _db.SaveChangesAsync();
        }

        private async Task SaveSnapshot(SearchResultViewModel model)
        {
            var uid = await GetUserId();
            var json = JsonConvert.SerializeObject(model);
            var existing = await _db.SearchSnapshots.FindAsync(uid);
            if (existing != null) { existing.ResultJson = json; existing.SavedAt = DateTime.Now; }
            else _db.SearchSnapshots.Add(new SearchSnapshot { UserId = uid, ResultJson = json, SavedAt = DateTime.Now });
            await _db.SaveChangesAsync();
        }

        private async Task<SearchResultViewModel> LoadSnapshot()
        {
            var uid = await GetUserId();
            var s = await _db.SearchSnapshots.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
            return s == null || string.IsNullOrEmpty(s.ResultJson) ? null
                : JsonConvert.DeserializeObject<SearchResultViewModel>(s.ResultJson);
        }

        private async Task<List<string>> GetColumns()
        {
            var cols = await _db.IndexedRecords.AsNoTracking().Select(r => r.ColumnName).Distinct().OrderBy(c => c).ToListAsync();
            return cols.Count > 0 ? cols : new List<string> { "Building", "Unit", "Owner", "Price", "Status" };
        }

        private static void BuildResultModel(SearchResultViewModel model, List<IndexedRecord> matches)
        {
            foreach (var g in matches.GroupBy(r => r.IndexedFileId).OrderByDescending(g => g.Count()))
            {
                var f = g.FirstOrDefault()?.IndexedFile;
                if (f == null) continue;
                var fr = new SearchFileResult
                {
                    FileName = f.FileName, FilePath = f.FilePath,
                    Worksheet = f.Worksheets ?? "Data",
                    MatchCount = g.Count(),
                    SampleValue = g.FirstOrDefault()?.ColumnValue ?? "",
                    MatchedColumn = g.GroupBy(r => r.ColumnName).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key,
                    LastIndexed = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy HH:mm")
                };
                foreach (var rg in g.GroupBy(r => r.RowNumber).Take(50))
                {
                    var row = new Dictionary<string, string>();
                    foreach (var r in rg) row[r.ColumnName] = r.ColumnValue;
                    row["__source_row__"] = rg.Key.ToString();
                    fr.Rows.Add(row);
                }
                model.TotalMatches += fr.MatchCount;
                model.Files.Add(fr);
            }
        }
    }
}
