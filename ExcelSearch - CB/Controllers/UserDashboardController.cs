using ClosedXML.Excel;
using ExcelSearch___CB.Data;
using ExcelSearch___CB.Models;
using ExcelSearch___CB.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Controllers
{
    [Authorize]
    public class UserDashboardController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;
        private readonly FileIndexingService _indexing;

        private const int PageSize = 25;

        public UserDashboardController(
            IWebHostEnvironment env,
            AppDbContext db,
            UserManager<AppUser> userManager,
            FileIndexingService indexing)
        {
            _env = env;
            _db = db;
            _userManager = userManager;
            _indexing = indexing;
        }

        // ── Helpers ──────────────────────────────────────────────────

        private async Task<string> GetUserName()
        {
            var user = await _userManager.GetUserAsync(User);
            return user?.FullName ?? User.Identity?.Name ?? "User";
        }

        private async Task<string> GetUserId()
        {
            var user = await _userManager.GetUserAsync(User);
            return user?.Id ?? "";
        }

        // ── Search snapshot: DB-persisted, survives restarts ─────────

        private async Task SaveSearchSnapshot(SearchResultViewModel model)
        {
            var userId = await GetUserId();
            var json = JsonConvert.SerializeObject(model);

            var existing = await _db.SearchSnapshots.FindAsync(userId);
            if (existing != null)
            {
                existing.ResultJson = json;
                existing.SavedAt = DateTime.Now;
            }
            else
            {
                _db.SearchSnapshots.Add(new SearchSnapshot
                {
                    UserId = userId,
                    ResultJson = json,
                    SavedAt = DateTime.Now
                });
            }
            await _db.SaveChangesAsync();
        }

        private async Task<SearchResultViewModel> LoadSearchSnapshot()
        {
            var userId = await GetUserId();
            var snapshot = await _db.SearchSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId);

            if (snapshot == null || string.IsNullOrEmpty(snapshot.ResultJson))
                return null;

            return JsonConvert.DeserializeObject<SearchResultViewModel>(snapshot.ResultJson);
        }

        // ── Dashboard ───────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewBag.UserName = await GetUserName();
            ViewBag.FilesIndexed = await _db.IndexedFiles.AsNoTracking()
                .CountAsync(f => f.Status == "Indexed");
            ViewBag.TotalRecords = await _db.IndexedRecords.AsNoTracking().CountAsync();

            var userId = await GetUserId();
            ViewBag.MyExports = await _db.ExportLogs.AsNoTracking()
                .Where(e => e.UserId == userId
                    && e.ExportTime.Month == DateTime.Now.Month
                    && e.ExportTime.Year == DateTime.Now.Year)
                .CountAsync();
            ViewBag.SearchesToday = await _db.SearchLogs.AsNoTracking()
                .CountAsync(l => l.SearchTime.Date == DateTime.Today);

            ViewBag.TopSearches = await _db.SearchLogs.AsNoTracking()
                .Where(l => l.SearchTime >= DateTime.Today.AddDays(-7))
                .GroupBy(l => l.SearchTerm)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(4).ToListAsync();

            var recentSearches = await _db.SearchLogs.AsNoTracking()
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.SearchTime).Take(5)
                .Select(l => new { Type = "search", Text = l.SearchTerm, Time = l.SearchTime })
                .ToListAsync();

            var recentExports = await _db.ExportLogs.AsNoTracking()
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.ExportTime).Take(5)
                .Select(l => new { Type = "export", Text = l.FileName, Count = l.RowCount, Time = l.ExportTime })
                .ToListAsync();

            var activities = new List<object>();
            foreach (var s in recentSearches)
                activities.Add(new { Type = "search", Text = s.Text, Time = s.Time.ToString("dd MMM HH:mm"), TimeAgo = FormatTimeAgo(s.Time) });
            foreach (var e in recentExports)
                activities.Add(new { Type = "export", Text = e.Text, Count = e.Count, Time = e.Time.ToString("dd MMM HH:mm"), TimeAgo = FormatTimeAgo(e.Time) });
            ViewBag.Activities = activities.OrderByDescending(a => ((dynamic)a).Time).Take(5).ToList();

            return View();
        }

        // ── Filter Builder ──────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> FilterBuilder()
        {
            ViewBag.Columns = await GetDistinctColumns();
            return View();
        }

        // ── Search ──────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Search(string searchText)
        {
            ViewBag.SearchText = searchText;
            ViewBag.Columns = await GetDistinctColumns();
            ViewBag.FilesIndexed = await _db.IndexedFiles.AsNoTracking()
                .CountAsync(f => f.Status == "Indexed");

            var currentUserId = await GetUserId();
            ViewBag.RecentSearches = await _db.SearchLogs.AsNoTracking()
                .Where(l => l.UserId == currentUserId)
                .OrderByDescending(l => l.SearchTime)
                .Select(l => new { Term = l.SearchTerm, Mode = l.SearchMode, Column = l.SearchColumn ?? "All" })
                .Distinct().Take(5).ToListAsync();

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Search(string searchText, string searchColumn, string searchMode = "Partial")
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                TempData["SearchError"] = "Please enter something to search.";
                return RedirectToAction("Search");
            }

            var model = await ExecuteSearch(searchText, searchColumn, searchMode);

            if (model.Files.Count == 0)
            {
                TempData["SearchError"] = "No matching records found.";
                return RedirectToAction("Search");
            }

            // Persist to DB — survives server restart.
            await SaveSearchSnapshot(model);

            var userId = await GetUserId();
            _db.SearchLogs.Add(new SearchLog
            {
                UserId = userId,
                UserName = User.Identity?.Name ?? "User",
                SearchTerm = searchText,
                SearchMode = searchMode,
                SearchColumn = searchColumn ?? "All",
                ResultCount = model.TotalMatches,
                SearchTime = DateTime.Now
            });
            await _db.SaveChangesAsync();

            return View("SearchResults", model);
        }

        private async Task<SearchResultViewModel> ExecuteSearch(
            string searchText, string searchColumn, string searchMode)
        {
            var model = new SearchResultViewModel { SearchTerm = searchText };

            IQueryable<IndexedRecord> baseQuery = _db.IndexedRecords
                .AsNoTracking()
                .Include(r => r.IndexedFile);

            if (!string.IsNullOrWhiteSpace(searchColumn) && searchColumn != "All")
                baseQuery = baseQuery.Where(r => r.ColumnName == searchColumn);

            // DB-level filter — no artificial cap, database size is the only limit.
            List<IndexedRecord> matches;

            if (searchMode == "Exact")
            {
                matches = await baseQuery
                    .Where(r => r.ColumnValue.ToLower() == searchText.ToLower())
                    .OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber)
                    .ToListAsync();
            }
            else if (searchMode == "Flexible")
            {
                // Flexible: strip spaces/hyphens/underscores then Contains match.
                // Build a LIKE pattern with wildcards between chars to span gaps.
                var stripped = searchText
                    .Replace(" ", "").Replace("-", "").Replace("_", "").ToLower();
                var likePattern = "%" + string.Join("%", stripped.ToCharArray()) + "%";

                matches = await baseQuery
                    .Where(r => EF.Functions.Like(
                        r.ColumnValue.Replace(" ", "").Replace("-", "").Replace("_", "").ToLower(),
                        likePattern))
                    .OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber)
                    .ToListAsync();
            }
            else // Partial
            {
                matches = await baseQuery
                    .Where(r => r.ColumnValue.ToLower().Contains(searchText.ToLower()))
                    .OrderBy(r => r.IndexedFileId).ThenBy(r => r.RowNumber)
                    .ToListAsync();
            }

            BuildResultModel(model, matches);
            return model;
        }

        // ── Filter Search ───────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> FilterSearch(string conditions)
        {
            if (string.IsNullOrWhiteSpace(conditions))
            {
                TempData["SearchError"] = "No filter conditions provided.";
                return RedirectToAction("Search");
            }

            var filters = JsonConvert.DeserializeObject<List<FilterCondition>>(conditions);
            HashSet<(int FileId, int RowNum)> matchingRows = null;

            foreach (var filter in filters)
            {
                IQueryable<IndexedRecord> condQuery = _db.IndexedRecords
                    .AsNoTracking()
                    .Where(r => r.ColumnName == filter.Field);

                string val = filter.Value.ToLower();

                condQuery = filter.Operator switch
                {
                    "Contains"    => condQuery.Where(r => r.ColumnValue.ToLower().Contains(val)),
                    "Equals"      => condQuery.Where(r => r.ColumnValue.ToLower() == val),
                    "Greater than" => condQuery.Where(r => r.ColumnValue.CompareTo(filter.Value) > 0),
                    "Less than"   => condQuery.Where(r => r.ColumnValue.CompareTo(filter.Value) < 0),
                    _             => condQuery.Where(r => r.ColumnValue.ToLower().Contains(val))
                };

                var condMatches = await condQuery
                    .Select(r => new { r.IndexedFileId, r.RowNumber })
                    .Distinct()
                    .ToListAsync();

                var set = new HashSet<(int, int)>(
                    condMatches.Select(m => (m.IndexedFileId, m.RowNumber)));

                if (matchingRows == null)
                    matchingRows = set;
                else
                    matchingRows.IntersectWith(set);

                if (matchingRows.Count == 0) break;
            }

            var model = new SearchResultViewModel
            {
                SearchTerm = string.Join(" · ",
                    filters.Select(f => f.Field + " " + f.Operator + " " + f.Value))
            };

            if (matchingRows != null && matchingRows.Count > 0)
            {
                var fileIds = matchingRows.Select(r => r.FileId).Distinct().ToList();
                var rowNums = matchingRows.Select(r => r.RowNum).Distinct().ToList();

                var allMatches = await _db.IndexedRecords
                    .AsNoTracking()
                    .Include(r => r.IndexedFile)
                    .Where(r => fileIds.Contains(r.IndexedFileId)
                             && rowNums.Contains(r.RowNumber))
                    .ToListAsync();

                allMatches = allMatches
                    .Where(r => matchingRows.Contains((r.IndexedFileId, r.RowNumber)))
                    .ToList();

                BuildResultModel(model, allMatches);
            }

            if (model.Files.Count == 0)
            {
                TempData["SearchError"] = "No matching records found.";
                return RedirectToAction("Search");
            }

            await SaveSearchSnapshot(model);

            var userId = await GetUserId();
            _db.SearchLogs.Add(new SearchLog
            {
                UserId = userId,
                UserName = User.Identity?.Name ?? "User",
                SearchTerm = model.SearchTerm,
                SearchMode = "Filter",
                SearchColumn = "",
                ResultCount = model.TotalMatches,
                SearchTime = DateTime.Now
            });
            await _db.SaveChangesAsync();

            return View("SearchResults", model);
        }

        // ── Search Results (paginated, DB-backed) ───────────────────

        [HttpGet]
        public async Task<IActionResult> SearchResults(int page = 1)
        {
            var model = await LoadSearchSnapshot();

            if (model == null)
            {
                TempData["SearchError"] =
                    "No search results available. Perform a search first.";
                return RedirectToAction("Search");
            }

            model.CurrentPage = page;
            model.PageSize = PageSize;
            model.TotalPages = (int)Math.Ceiling((double)model.Files.Count / PageSize);
            model.Files = model.Files.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            return View(model);
        }

        // ── Preview ─────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Preview(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    TempData["SearchError"] = "File not found.";
                    return RedirectToAction("Search");
                }

                ViewBag.FilePath = path;
                ViewBag.FileName = Path.GetFileName(path);

                var (rows, _) = _indexing.PreviewFile(path, 500);
                return View(rows);
            }
            catch (Exception)
            {
                TempData["SearchError"] = "Unable to preview this file.";
                return RedirectToAction("Search");
            }
        }

        // ── Export (DB-backed search results = survives restarts) ───

        [HttpGet]
        public async Task<IActionResult> ExportCurrentFile(string path, string format = "xlsx")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    TempData["SearchError"] = "Export file not found.";
                    return RedirectToAction("Search");
                }

                // Load search results from DB snapshot.
                var searchModel = await LoadSearchSnapshot();

                string exportFolder = Path.Combine(_env.WebRootPath, "Exports");
                if (!Directory.Exists(exportFolder))
                    Directory.CreateDirectory(exportFolder);

                string originalName = Path.GetFileNameWithoutExtension(path);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var userId = await GetUserId();

                return format == "csv"
                    ? await ExportCsv(path, exportFolder, originalName, timestamp, searchModel, userId)
                    : await ExportXlsx(path, exportFolder, originalName, timestamp, searchModel, userId);
            }
            catch (Exception ex)
            {
                TempData["SearchError"] = "Export failed: " + ex.Message;
                return RedirectToAction("Search");
            }
        }

        private async Task<IActionResult> ExportXlsx(string path, string exportFolder,
            string originalName, string timestamp,
            SearchResultViewModel searchModel, string userId)
        {
            string exportFileName = timestamp + "_" + originalName + ".xlsx";
            string exportPath = Path.Combine(exportFolder, exportFileName);
            int rowCount = 0;

            await Task.Run(() =>
            {
                using var wb = new XLWorkbook();

                if (searchModel != null)
                {
                    var fileResult = searchModel.Files.FirstOrDefault(f => f.FilePath == path);
                    if (fileResult != null && fileResult.Rows.Any())
                    {
                        var ws = wb.Worksheets.Add("Search Results");
                        var headers = fileResult.Rows.First().Keys.ToList();
                        for (int c = 0; c < headers.Count; c++)
                            ws.Cell(1, c + 1).Value = headers[c];
                        for (int r = 0; r < fileResult.Rows.Count; r++)
                        {
                            for (int c = 0; c < headers.Count; c++)
                                ws.Cell(r + 2, c + 1).Value =
                                    fileResult.Rows[r].TryGetValue(headers[c], out var val) ? val : "";
                            rowCount++;
                        }
                    }
                }

                if (rowCount == 0)
                {
                    var ws = wb.Worksheets.Add("Export");
                    ws.Cell(1, 1).Value = "Export from: " + Path.GetFileName(path);
                    rowCount = 1;
                }

                wb.SaveAs(exportPath);
            });

            await LogExport(userId, exportFileName, exportPath, rowCount);

            byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(exportPath);
            return File(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                exportFileName);
        }

        private async Task<IActionResult> ExportCsv(string path, string exportFolder,
            string originalName, string timestamp,
            SearchResultViewModel searchModel, string userId)
        {
            string exportFileName = timestamp + "_" + originalName + ".csv";
            string exportPath = Path.Combine(exportFolder, exportFileName);
            int rowCount = 0;

            await Task.Run(() =>
            {
                if (searchModel != null)
                {
                    var fileResult = searchModel.Files.FirstOrDefault(f => f.FilePath == path);
                    if (fileResult != null && fileResult.Rows.Any())
                    {
                        var headers = fileResult.Rows.First().Keys.ToList();
                        var sb = new StringBuilder();
                        sb.AppendLine(string.Join(",", headers));
                        foreach (var row in fileResult.Rows)
                        {
                            var values = headers.Select(h =>
                                "\"" + (row.TryGetValue(h, out var v) ? v : "").Replace("\"", "\"\"") + "\"");
                            sb.AppendLine(string.Join(",", values));
                            rowCount++;
                        }
                        System.IO.File.WriteAllText(exportPath, sb.ToString(), Encoding.UTF8);
                    }
                }

                if (rowCount == 0)
                {
                    System.IO.File.Copy(path, exportPath, true);
                    rowCount = (int)new FileInfo(exportPath).Length;
                }
            });

            await LogExport(userId, exportFileName, exportPath, rowCount);

            byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(exportPath);
            return File(fileBytes, "text/csv", exportFileName);
        }

        private async Task LogExport(string userId, string fileName,
            string exportPath, int rowCount)
        {
            _db.ExportLogs.Add(new ExportLog
            {
                UserId = userId,
                UserName = User.Identity?.Name ?? "User",
                FileName = fileName,
                ExportPath = exportPath,
                RowCount = rowCount,
                Status = "Completed",
                ExportTime = DateTime.Now
            });
            await _db.SaveChangesAsync();
        }

        // ── My Exports ──────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> MyExports()
        {
            var userId = await GetUserId();
            var exports = await _db.ExportLogs.AsNoTracking()
                .Where(e => e.UserId == userId)
                .OrderByDescending(e => e.ExportTime)
                .ToListAsync();

            var model = new UserExportViewModel
            {
                Exports = exports.Select(e => new ExportItem
                {
                    FileName = e.FileName,
                    ExportTime = e.ExportTime.ToString("dd MMM yyyy HH:mm"),
                    Status = e.Status
                }).ToList()
            };

            return View(model);
        }

        [HttpGet]
        public IActionResult DownloadExport(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    TempData["SearchError"] = "Export file name is missing.";
                    return RedirectToAction("MyExports");
                }

                string filePath = Path.Combine(_env.WebRootPath, "Exports", fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    TempData["SearchError"] = "Export file no longer exists.";
                    return RedirectToAction("MyExports");
                }

                byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                string contentType = fileName.EndsWith(".csv")
                    ? "text/csv"
                    : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                return File(fileBytes, contentType, fileName);
            }
            catch (Exception ex)
            {
                TempData["SearchError"] = "Unable to download: " + ex.Message;
                return RedirectToAction("MyExports");
            }
        }

        // ── Shared helpers ───────────────────────────────────────────

        private async Task<List<string>> GetDistinctColumns()
        {
            var columns = await _db.IndexedRecords.AsNoTracking()
                .Select(r => r.ColumnName).Distinct().OrderBy(c => c).ToListAsync();
            return columns.Count > 0
                ? columns
                : new List<string> { "Building", "Unit", "Owner", "Price", "Status" };
        }

        private static void BuildResultModel(
            SearchResultViewModel model, List<IndexedRecord> matches)
        {
            var fileGroups = matches
                .GroupBy(r => r.IndexedFile)
                .OrderByDescending(g => g.Count());

            foreach (var group in fileGroups)
            {
                var file = group.Key;
                var fileResult = new SearchFileResult
                {
                    FileName = file.FileName,
                    FilePath = file.FilePath,
                    Worksheet = file.Worksheets ?? "Data",
                    MatchCount = group.Count(),
                    SampleValue = group.FirstOrDefault()?.ColumnValue ?? "",
                    LastIndexed = (file.LastIndexedAt ?? file.UploadedAt)
                        .ToString("dd MMM yyyy HH:mm")
                };

                var rowGroups = group.GroupBy(r => r.RowNumber).Take(50);
                foreach (var rowGroup in rowGroups)
                {
                    var row = new Dictionary<string, string>();
                    foreach (var record in rowGroup)
                        row[record.ColumnName] = record.ColumnValue;
                    fileResult.Rows.Add(row);
                }

                model.TotalMatches += fileResult.MatchCount;
                model.Files.Add(fileResult);
            }
        }

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

    public class FilterCondition
    {
        public string Field { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
    }
}
