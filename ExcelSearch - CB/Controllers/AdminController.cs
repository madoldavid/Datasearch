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
    [Authorize]
    public class AdminController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;
        private readonly FileIndexingService _indexing;
        private readonly IConfiguration _config;

        public AdminController(
            IWebHostEnvironment env,
            AppDbContext db,
            UserManager<AppUser> userManager,
            FileIndexingService indexing,
            IConfiguration config)
        {
            _env = env;
            _db = db;
            _userManager = userManager;
            _indexing = indexing;
            _config = config;
        }

        private string UploadsPath => Path.Combine(_env.WebRootPath, "Uploads");

        private async Task<(string Name, string Role)> GetCurrentUserInfo()
        {
            var user = await _userManager.GetUserAsync(User);
            var name = user?.FullName ?? User.Identity?.Name ?? "User";
            var roles = await _userManager.GetRolesAsync(user!);
            var role = roles.Count > 0 ? string.Join(", ", roles) : "User";
            return (name, role);
        }

        public IActionResult Index() => RedirectToAction("Overview");

        // ── Overview ─────────────────────────────────────────────────────

        public async Task<IActionResult> Overview()
        {
            var (name, role) = await GetCurrentUserInfo();

            var files = await _db.IndexedFiles.AsNoTracking().ToListAsync();
            var totalRecords = await _db.IndexedRecords.AsNoTracking().CountAsync();
            var failedCount = files.Count(f => f.Status == "Failed");
            var indexedCount = files.Count(f => f.Status == "Indexed");
            var totalBytes = files.Sum(f => f.FileSize);
            var storageLimit = _config.GetValue<double>("Storage:LimitGB", 50);

            var model = new OverviewViewModel
            {
                AdminName = name,
                AdminRole = role,
                FilesIndexed = indexedCount,
                FilesIndexedDescription = files.Count + " files uploaded",
                TotalRecords = totalRecords,
                TotalRecordsDescription = "Across all sheets",
                FailedFiles = failedCount,
                FailedFilesDescription = failedCount == 0 ? "No failed files" : failedCount + " need attention",
                StorageUsed = (decimal)Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                StorageLimit = (decimal)storageLimit,
                LastIndexTime = files.Any() ? files.Max(f => f.LastIndexedAt ?? f.UploadedAt) : DateTime.Now,
                PipelineStages = new List<PipelineStageViewModel>
                {
                    new() { StageName = "Upload",  Details = files.Count + " files",       Status = files.Count > 0 ? "Ready" : "Waiting" },
                    new() { StageName = "Validate", Details = indexedCount + " passed",      Status = indexedCount > 0 ? "Ready" : "Waiting" },
                    new() { StageName = "Store",    Details = "Database",                    Status = "Ready" },
                    new() { StageName = "Index",    Details = totalRecords + " records",     Status = totalRecords > 0 ? "Complete" : "Pending" },
                    new() { StageName = "Ready",    Details = indexedCount + "/" + files.Count, Status = indexedCount == files.Count && files.Count > 0 ? "Complete" : "Pending" }
                }
            };

            return View(model);
        }

        // ── Upload ───────────────────────────────────────────────────────

        public async Task<IActionResult> Upload()
        {
            var (name, role) = await GetCurrentUserInfo();
            var recent = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.UploadedAt).Take(10).ToListAsync();

            var model = new UploadViewModel
            {
                AdminName = name,
                AdminRole = role,
                RecentUploads = recent.Select(f => new UploadedFileViewModel
                {
                    FileName = f.FileName,
                    FileSize = FormatSize(f.FileSize),
                    RowCount = f.RowCount,
                    UploadedTime = FormatTimeAgo(f.UploadedAt),
                    Status = f.Status
                }).ToList()
            };

            return View(model);
        }

        [HttpPost]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> StartIndexing(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["Message"] = "Please select an Excel or CSV file.";
                return RedirectToAction("Upload");
            }

            string ext = Path.GetExtension(excelFile.FileName).ToLower();
            if (ext != ".xlsx" && ext != ".xls" && ext != ".csv")
            {
                TempData["Message"] = "Invalid file format: " + ext + ". Use .XLSX, .XLS, or .CSV.";
                return RedirectToAction("Upload");
            }

            if (!Directory.Exists(UploadsPath))
                Directory.CreateDirectory(UploadsPath);

            string fileName = Path.GetFileName(excelFile.FileName);
            string path = Path.Combine(UploadsPath, fileName);

            // Streaming save — never buffers the entire file in memory.
            await using (var stream = new FileStream(path, FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await excelFile.CopyToAsync(stream);
            }

            try
            {
                _indexing.IndexFile(path, fileName);
                TempData["Message"] = "File uploaded and indexed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Message"] = "File uploaded but indexing failed: " + ex.Message;
            }

            return RedirectToAction("IndexMonitor");
        }

        // ── Index Monitor ────────────────────────────────────────────────

        public async Task<IActionResult> IndexMonitor()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking().ToListAsync();
            var completed = files.Count(f => f.Status == "Indexed");
            var failed = files.Count(f => f.Status == "Failed");
            var processing = files.Count(f => f.Status == "Indexing");
            var totalRows = files.Sum(f => f.RowCount);

            var model = new IndexMonitorViewModel
            {
                AdminName = name,
                AdminRole = role,
                QueueFiles = files.Count,
                ProcessingFiles = processing,
                CompletedFiles = completed,
                FailedFiles = failed,
                CurrentFile = files.FirstOrDefault(f => f.Status == "Indexing")?.FileName
                    ?? (files.Any() ? files.Last().FileName : "No files"),
                CurrentWorksheet = "-",
                RowsProcessed = totalRows,
                TotalRows = totalRows,
                CompletionPercentage = files.Count > 0 ? (int)((double)completed / files.Count * 100) : 0,
                EstimatedTime = completed == files.Count ? "All complete"
                    : processing > 0 ? "Processing..." : "~" + (files.Count - completed) + " remaining",
                QueueItems = files.Select(f => new IndexQueueItemViewModel
                {
                    FileName = f.FileName,
                    Status = f.Status
                }).ToList(),
                Activities = files.OrderByDescending(f => f.UploadedAt).Take(10).Select(f =>
                    new IndexActivityViewModel
                    {
                        Message = f.Status == "Indexed" ? "Indexing completed"
                            : f.Status == "Failed" ? "Indexing failed — " + (f.ErrorMessage ?? "Unknown error")
                            : f.Status == "Indexing" ? "Indexing in progress"
                            : "File uploaded",
                        Time = FormatTimeAgo(f.UploadedAt)
                    }).ToList()
            };

            return View(model);
        }

        // ── All Files ────────────────────────────────────────────────────

        public async Task<IActionResult> AllFiles()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking()
                .OrderByDescending(f => f.UploadedAt).ToListAsync();

            var model = new AllFilesViewModel
            {
                AdminName = name,
                AdminRole = role,
                TotalFiles = files.Count,
                SummaryText = files.Count + " files tracked",
                Files = files.Select(f => new FileItemViewModel
                {
                    FileName = f.FileName,
                    FileSize = FormatSize(f.FileSize),
                    LastIndexed = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy, HH:mm"),
                    Records = f.RowCount,
                    Status = f.Status
                }).ToList()
            };

            return View(model);
        }

        // ── Failed Files ─────────────────────────────────────────────────

        public async Task<IActionResult> FailedFiles()
        {
            var (name, role) = await GetCurrentUserInfo();
            var failed = await _db.IndexedFiles.AsNoTracking()
                .Where(f => f.Status == "Failed")
                .OrderByDescending(f => f.UploadedAt).ToListAsync();

            var model = new FailedFilesViewModel
            {
                AdminName = name,
                AdminRole = role,
                TotalFailedFiles = failed.Count,
                Description = failed.Count > 0
                    ? "Files that could not be indexed. Retry or delete them."
                    : "No issues — all files indexed successfully.",
                Files = failed.Select(f => new FailedFileItemViewModel
                {
                    FileName = f.FileName,
                    Reason = f.ErrorMessage ?? "Unknown error",
                    FileSize = FormatSize(f.FileSize),
                    LastAttempt = (f.LastIndexedAt ?? f.UploadedAt).ToString("dd MMM yyyy, HH:mm")
                }).ToList()
            };

            return View(model);
        }

        // ── Search History ───────────────────────────────────────────────

        public async Task<IActionResult> SearchHistory()
        {
            var (name, role) = await GetCurrentUserInfo();
            var logs = await _db.SearchLogs.AsNoTracking()
                .OrderByDescending(l => l.SearchTime).Take(500).ToListAsync();

            var today = DateTime.Today;
            var todayLogs = logs.Where(l => l.SearchTime.Date == today).ToList();

            var model = new SearchHistoryViewModel
            {
                AdminName = name,
                AdminRole = role,
                TotalSearches = await _db.SearchLogs.AsNoTracking().CountAsync(),
                TodaySearches = await _db.SearchLogs.AsNoTracking()
                    .CountAsync(l => l.SearchTime.Date == today),
                AverageResults = await _db.SearchLogs.AsNoTracking().AnyAsync()
                    ? (int)await _db.SearchLogs.AsNoTracking().AverageAsync(l => (double)l.ResultCount) : 0,
                MostPopularSearch = await _db.SearchLogs.AsNoTracking()
                    .GroupBy(l => l.SearchTerm)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefaultAsync() ?? "-",
                Searches = logs.Select(l => new SearchHistoryItemViewModel
                {
                    UserName = l.UserName ?? "User",
                    SearchTerm = l.SearchTerm,
                    SearchMode = l.SearchMode,
                    Results = l.ResultCount,
                    SearchTime = l.SearchTime.ToString("dd MMM yyyy HH:mm")
                }).ToList()
            };

            return View(model);
        }

        // ── Export History ───────────────────────────────────────────────

        public async Task<IActionResult> ExportHistory()
        {
            var (name, role) = await GetCurrentUserInfo();
            var logs = await _db.ExportLogs.AsNoTracking()
                .OrderByDescending(l => l.ExportTime).Take(500).ToListAsync();

            var todayLogs = logs.Where(l => l.ExportTime.Date == DateTime.Today).ToList();
            var weekLogs = logs.Where(l => l.ExportTime >= DateTime.Today.AddDays(-7)).ToList();
            long totalBytes = 0;

            foreach (var log in logs)
            {
                if (System.IO.File.Exists(log.ExportPath))
                    totalBytes += new FileInfo(log.ExportPath).Length;
            }

            var model = new ExportHistoryViewModel
            {
                AdminName = name,
                AdminRole = role,
                TotalExports = await _db.ExportLogs.AsNoTracking().CountAsync(),
                TodayExports = await _db.ExportLogs.AsNoTracking()
                    .CountAsync(l => l.ExportTime.Date == DateTime.Today),
                WeeklyExports = await _db.ExportLogs.AsNoTracking()
                    .CountAsync(l => l.ExportTime >= DateTime.Today.AddDays(-7)),
                TotalSize = FormatSize(totalBytes),
                Exports = logs.Select(l => new ExportHistoryItemViewModel
                {
                    FileName = l.FileName,
                    ExportedBy = l.UserName ?? "User",
                    Rows = l.RowCount,
                    ExportTime = l.ExportTime.ToString("dd MMM yyyy HH:mm"),
                    Status = l.Status
                }).ToList()
            };

            return View(model);
        }

        // ── Storage ──────────────────────────────────────────────────────

        public async Task<IActionResult> Storage()
        {
            var (name, role) = await GetCurrentUserInfo();
            var files = await _db.IndexedFiles.AsNoTracking()
                .Where(f => f.Status == "Indexed").ToListAsync();

            long totalBytes = files.Sum(f => f.FileSize);
            double storageLimitGB = _config.GetValue<double>("Storage:LimitGB", 50);
            double usedGB = totalBytes / (1024.0 * 1024.0 * 1024.0);
            int percentage = storageLimitGB > 0 ? (int)((usedGB / storageLimitGB) * 100) : 0;
            if (percentage > 100) percentage = 100;

            var uploadFiles = new List<StorageFileViewModel>();
            if (Directory.Exists(UploadsPath))
            {
                foreach (var f in Directory.GetFiles(UploadsPath).Take(10))
                {
                    var fi = new FileInfo(f);
                    uploadFiles.Add(new StorageFileViewModel
                    {
                        FileName = fi.Name,
                        FileSize = FormatSize(fi.Length)
                    });
                }
            }

            var dbSize = System.IO.File.Exists(dbPath())
                ? FormatSize(new FileInfo(dbPath()).Length) : "Unknown";

            var model = new StorageViewModel
            {
                AdminName = name,
                AdminRole = role,
                StorageUsed = Math.Round(usedGB, 2) + " GB",
                StorageLimit = storageLimitGB + " GB",
                StoragePercentage = percentage,
                TotalFiles = files.Count,
                DatabaseSize = dbSize,
                TotalRecords = (await _db.IndexedRecords.AsNoTracking().CountAsync()).ToString("N0"),
                LastBackup = "Not Configured",
                Files = uploadFiles
            };

            return View(model);
        }

        // ── Actions ──────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return RedirectToAction("AllFiles");

            var file = await _db.IndexedFiles.FirstOrDefaultAsync(f => f.FileName == fileName);
            if (file != null)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", file.Id);
                _db.IndexedFiles.Remove(file);
                await _db.SaveChangesAsync();
            }

            string physicalPath = Path.Combine(UploadsPath, fileName);
            if (System.IO.File.Exists(physicalPath))
                System.IO.File.Delete(physicalPath);

            TempData["Message"] = "File deleted successfully.";
            return RedirectToAction("AllFiles");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReindexFile(string fileName)
        {
            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path))
            {
                TempData["Message"] = "File not found on disk.";
                return RedirectToAction("AllFiles");
            }

            try
            {
                _indexing.IndexFile(path, fileName);
                TempData["Message"] = "File re-indexed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Message"] = "Re-index failed: " + ex.Message;
            }

            return RedirectToAction("AllFiles");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> RetryFile(string fileName)
            => ReindexFile(fileName);

        public IActionResult ViewFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return RedirectToAction("AllFiles");

            string path = Path.Combine(UploadsPath, fileName);
            if (!System.IO.File.Exists(path))
            {
                TempData["Message"] = "File not found.";
                return RedirectToAction("AllFiles");
            }

            return RedirectToAction("Preview", "UserDashboard", new { path });
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private string dbPath() => Path.Combine(_env.ContentRootPath, "App_Data", "ExcelSearch.db");

        private static string FormatSize(long bytes) => bytes switch
        {
            > 1_073_741_824L => Math.Round(bytes / 1_073_741_824.0, 2) + " GB",
            > 1_048_576L => Math.Round(bytes / 1_048_576.0, 2) + " MB",
            > 1_024L => Math.Round(bytes / 1_024.0, 2) + " KB",
            > 0 => bytes + " bytes",
            _ => "0 KB"
        };

        private static string FormatTimeAgo(DateTime dt)
        {
            var span = DateTime.Now - dt;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + " hr" + (span.TotalHours >= 2 ? "s" : "") + " ago";
            if (span.TotalDays < 7) return (int)span.TotalDays + " day" + (span.TotalDays >= 2 ? "s" : "") + " ago";
            return dt.ToString("dd MMM yyyy");
        }
    }
}
