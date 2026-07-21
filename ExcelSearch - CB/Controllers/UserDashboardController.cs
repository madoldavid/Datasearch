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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Controllers
{
    [Authorize(Roles = "User")]
    public class UserDashboardController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;
        private readonly FileIndexingService _indexing;
        private readonly ConfigurationService _configService;
        private const int PageSize = 25;

        public UserDashboardController(IWebHostEnvironment env, AppDbContext db,
            UserManager<AppUser> userManager, FileIndexingService indexing,
            ConfigurationService configService)
        { _env = env; _db = db; _userManager = userManager; _indexing = indexing; _configService = configService; }

        private async Task<string> GetUserName()
        {
            var u = await _userManager.GetUserAsync(User);
            return u?.FullName ?? User.Identity?.Name ?? "User";
        }
        private async Task<string> GetUserId()
        {
            var u = await _userManager.GetUserAsync(User);
            return u?.Id ?? "";
        }

        // ── Dashboard ───────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var config = await _configService.GetAppConfig();
            var dashboardStrings = await _configService.GetStringsByPage("Index");

            ViewBag.AppConfig = config;
            ViewBag.UIStrings = dashboardStrings;
            ViewBag.UserName = await GetUserName();
            ViewBag.FilesIndexed = await _db.IndexedFiles.AsNoTracking().CountAsync(f => f.Status == "Indexed");
            ViewBag.TotalRecords = await _db.IndexedRecords.AsNoTracking().CountAsync();
            var uid = await GetUserId();
            ViewBag.MyExports = await _db.ExportLogs.AsNoTracking().CountAsync(e => e.UserId == uid && e.ExportTime.Month == DateTime.Now.Month);
            ViewBag.SearchesToday = await _db.SearchLogs.AsNoTracking().CountAsync(l => l.SearchTime.Date == DateTime.Today);
            ViewBag.TopSearches = await _db.SearchLogs.AsNoTracking().Where(l => l.SearchTime >= DateTime.Today.AddDays(-7)).GroupBy(l => l.SearchTerm).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(4).ToListAsync();

            var rs = await _db.SearchLogs.AsNoTracking().Where(l => l.UserId == uid).OrderByDescending(l => l.SearchTime).Take(5).ToListAsync();
            var re = await _db.ExportLogs.AsNoTracking().Where(l => l.UserId == uid).OrderByDescending(l => l.ExportTime).Take(5).ToListAsync();
            var acts = new List<object>();
            foreach (var s in rs) acts.Add(new { Type = "search", Text = s.SearchTerm, Time = s.SearchTime.ToString("dd MMM HH:mm"), TimeAgo = FmtAgo(s.SearchTime) });
            foreach (var e in re) acts.Add(new { Type = "export", Text = e.FileName, Count = e.RowCount, Time = e.ExportTime.ToString("dd MMM HH:mm"), TimeAgo = FmtAgo(e.ExportTime) });
            ViewBag.Activities = acts.OrderByDescending(a => ((dynamic)a).Time).Take(5).ToList();
            return View();
        }

        // ── Filter Builder ──────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> FilterBuilder()
        { ViewBag.Columns = await GetColumns(); return View(); }

        // ── Search page ─────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Search(string searchText)
        {
            ViewBag.SearchText = searchText;
            ViewBag.Columns = await GetColumns();
            ViewBag.FilesIndexed = await _db.IndexedFiles.AsNoTracking().CountAsync(f => f.Status == "Indexed");
            var uid = await GetUserId();
            ViewBag.RecentSearches = await _db.SearchLogs.AsNoTracking().Where(l => l.UserId == uid).OrderByDescending(l => l.SearchTime).Select(l => new { Term = l.SearchTerm, Mode = l.SearchMode, Column = l.SearchColumn ?? "All" }).Distinct().Take(5).ToListAsync();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Search(string searchText, string searchColumn, string searchMode = "Partial")
        {
            if (string.IsNullOrWhiteSpace(searchText))
            { TempData["SearchError"] = "Please enter something to search."; return RedirectToAction("Search"); }

            var model = await ExecuteSearch(searchText, searchColumn, searchMode, null);
            if (model.Files.Count == 0)
            { TempData["SearchError"] = "No matching records found."; return RedirectToAction("Search"); }

            await SaveSnapshot(model);
            _db.SearchLogs.Add(new SearchLog { UserId = await GetUserId(), UserName = User.Identity?.Name ?? "User", SearchTerm = searchText, SearchMode = searchMode, SearchColumn = searchColumn ?? "All", ResultCount = model.TotalMatches, SearchTime = DateTime.Now });
            await _db.SaveChangesAsync();
            return View("SearchResults", model);
        }

        // ── Filter Search ───────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> FilterSearch(string conditions, string logic = "AND")
        {
            if (string.IsNullOrWhiteSpace(conditions))
            { TempData["SearchError"] = "No filter conditions."; return RedirectToAction("Search"); }

            var filterGroups = JsonConvert.DeserializeObject<List<List<FilterCondition>>>(conditions);
            if (filterGroups == null || filterGroups.Count == 0)
            { TempData["SearchError"] = "No filter conditions."; return RedirectToAction("Search"); }

            var skippedSheets = new List<string>();
            HashSet<(int FileId, int RowNum)> allResults = null;

            // Process each AND group (outer level = AND, inner level = OR)
            foreach (var andGroup in filterGroups)
            {
                HashSet<(int FileId, int RowNum)> groupResults = null;

                foreach (var filter in andGroup)
                {
                    var (rowSet, fieldExists) = await EvaluateFilter(filter);
                    if (!fieldExists)
                        skippedSheets.Add(filter.Field);

                    if (rowSet != null)
                    {
                        if (groupResults == null) groupResults = rowSet;
                        else groupResults.UnionWith(rowSet); // OR within group
                    }
                }

                if (groupResults != null)
                {
                    if (allResults == null) allResults = groupResults;
                    else allResults.IntersectWith(groupResults); // AND between groups
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
            _db.SearchLogs.Add(new SearchLog { UserId = await GetUserId(), UserName = User.Identity?.Name ?? "User", SearchTerm = model.SearchTerm, SearchMode = "Filter", SearchColumn = "", ResultCount = model.TotalMatches, SearchTime = DateTime.Now });
            await _db.SaveChangesAsync();
            return View("SearchResults", model);
        }

        // ── Filter evaluation engine ─────────────────────────────────

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

        private IQueryable<IndexedRecord> NumericWhere(IQueryable<IndexedRecord> q,
            string val, string op)
        {
            // SQLite can't translate PadLeft — use string CompareTo at DB level,
            // then do numeric-aware post-filter in memory if needed.
            // For uniform-width values (common in real estate: FLAT NUMBER=4 digits),
            // CompareTo works correctly.
            // We fetch candidate rows, then post-filter with proper numeric comparison.
            if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
            {
                // Fetch all candidates with basic string ordering, then post-filter
                var candidates = q.Where(r => r.ColumnValue.TrimStart().Length > 0)
                    .Select(r => r.ColumnValue).Distinct().ToList();

                // Numeric-aware pad-left comparison in memory
                var paddedVal = val.TrimStart().PadLeft(20, '0');
                var matchingValues = candidates
                    .Where(v =>
                    {
                        var trimmed = v.TrimStart();
                        if (trimmed.Length == 0) return false;
                        // Only compare as numeric if both look numeric
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
                    })
                    .ToList();

                if (matchingValues.Count == 0) return q.Where(_ => false);
                return q.Where(r => matchingValues.Contains(r.ColumnValue));
            }
            // Not numeric — string compare fallback
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
            // Split: try ".." first, then " to ", then "," — don't use "-" as it breaks dates
            var parts = val.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length < 2) parts = val.Split(new[] { " to " }, StringSplitOptions.None);
            if (parts.Length < 2) parts = val.Split(new[] { "," }, StringSplitOptions.None);
            if (parts.Length < 2) return q.Take(0);

            var lo = parts[0].Trim();
            var hi = parts[1].Trim();

            // Try date range
            if (DateTime.TryParse(lo, out DateTime loDate) && DateTime.TryParse(hi, out DateTime hiDate))
            {
                var loStr = loDate.ToString("yyyyMMdd");
                var hiStr = hiDate.ToString("yyyyMMdd");

                var matches = q
                    .Select(r => r.ColumnValue).Distinct().ToList()
                    .Where(v =>
                    {
                        if (DateTime.TryParse(v, out DateTime cellDate))
                        {
                            var cellStr = cellDate.ToString("yyyyMMdd");
                            return string.Compare(cellStr, loStr, StringComparison.Ordinal) >= 0
                                && string.Compare(cellStr, hiStr, StringComparison.Ordinal) <= 0;
                        }
                        return false;
                    })
                    .ToList();

                if (matches.Count == 0) return q.Where(_ => false);
                return q.Where(r => matches.Contains(r.ColumnValue));
            }

            // Try numeric range: client-side pad-left for correct ordering
            if (double.TryParse(lo, out double _) && double.TryParse(hi, out double _))
            {
                var paddedLo = lo.PadLeft(20, '0');
                var paddedHi = hi.PadLeft(20, '0');

                var matches = q
                    .Select(r => r.ColumnValue).Distinct().ToList()
                    .Where(v =>
                    {
                        var trimmed = v.TrimStart();
                        if (trimmed.Length == 0) return false;
                        if (!trimmed.All(c => char.IsDigit(c) || c == '.' || c == '-' || c == ',')) return false;
                        var padded = trimmed.PadLeft(20, '0');
                        return padded.CompareTo(paddedLo) >= 0 && padded.CompareTo(paddedHi) <= 0;
                    })
                    .ToList();

                if (matches.Count == 0) return q.Where(_ => false);
                return q.Where(r => matches.Contains(r.ColumnValue));
            }

            // String range
            return q.Where(r => r.ColumnValue.CompareTo(lo) >= 0 && r.ColumnValue.CompareTo(hi) <= 0);
        }

        /// <summary>Detects if filter operators are date-related for special handling.</summary>
        private static bool IsLikelyDate(string val)
        {
            return DateTime.TryParse(val, out _)
                || val.Contains("/") || val.Contains("-")
                || (val.Length >= 8 && val.Length <= 10 && val.All(c => char.IsDigit(c) || c == '-'));
        }

        // ── Simple search ───────────────────────────────────────────

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

        // ── Search Results (paginated) ──────────────────────────────

        [HttpGet]
        public async Task<IActionResult> SearchResults(int page = 1)
        {
            var model = await LoadSnapshot();
            if (model == null)
            { TempData["SearchError"] = "No search results. Perform a search first."; return RedirectToAction("Search"); }
            model.CurrentPage = page; model.PageSize = PageSize;
            model.TotalPages = Math.Max(1, (int)Math.Ceiling((double)model.Files.Count / PageSize));
            model.Files = model.Files.Skip((page - 1) * PageSize).Take(PageSize).ToList();
            return View(model);
        }

        // ── Preview (matching rows only) ────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Preview(string path, bool matchingOnly = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !IsPathSafe(path) || !System.IO.File.Exists(path))
                { TempData["SearchError"] = "File not found or access denied."; return RedirectToAction("Search"); }

                ViewBag.FilePath = path;
                ViewBag.FileName = Path.GetFileName(path);
                ViewBag.MatchingOnly = matchingOnly;

                if (!matchingOnly)
                {
                    var (allRows, allHeaders) = _indexing.PreviewFile(path, 500);
                    ViewBag.SourceRowNumbers = null;
                    return View(allRows);
                }

                // Matching rows only — fetch from the search snapshot
                var model = await LoadSnapshot();
                if (model != null)
                {
                    var fileResult = model.Files.FirstOrDefault(f => f.FilePath == path);
                    if (fileResult != null)
                    {
                        // Load full row data for matched rows
                        var matchedRowNums = fileResult.Rows
                            .Select(r => r.ContainsKey("__source_row__") ? r["__source_row__"] : null)
                            .Where(r => r != null).ToList();

                        ViewBag.MatchedColumn = fileResult.SampleValue != null
                            ? "Sample: " + fileResult.SampleValue : null;
                        ViewBag.MatchCount = fileResult.Rows.Count;

                        // Show matched rows with source row numbers
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

                // Fallback: show all rows
                var (fallbackRows, _) = _indexing.PreviewFile(path, 500);
                return View(fallbackRows);
            }
            catch (Exception)
            { TempData["SearchError"] = "Unable to preview."; return RedirectToAction("Search"); }
        }

        // ── Export with grouping ────────────────────────────────────

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

                // Parse selected row indices if provided
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

        private async Task<IActionResult> ExportXlsx(string path, string folder,
            SearchResultViewModel model, string grouping, bool meta, HashSet<int> selected, string userId)
        {
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_")
                + Path.GetFileNameWithoutExtension(path) + ".xlsx";
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
                    else // flat
                    {
                        var ws = wb.Worksheets.Add("Search Results");
                        WriteRowsToSheet(ws, fileResult.Rows, meta, fileResult.FileName, fileResult.Worksheet, selected, ref totalRows);
                    }
                }

                if (totalRows == 0)
                { var ws = wb.Worksheets.Add("Export"); ws.Cell(1, 1).Value = "Export from: " + Path.GetFileName(path); totalRows = 1; }

                wb.SaveAs(dest);
            });

            await LogExport(userId, name, dest, totalRows, grouping,
                model?.SearchTerm ?? "", selected != null);
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(dest);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }

        private async Task<IActionResult> ExportCsv(string path, string folder,
            SearchResultViewModel model, string grouping, bool meta, HashSet<int> selected, string userId)
        {
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_")
                + Path.GetFileNameWithoutExtension(path) + ".csv";
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
                else
                { System.IO.File.Copy(path, dest, true); totalRows = (int)new FileInfo(dest).Length; }
            });

            await LogExport(userId, name, dest, totalRows, grouping,
                model?.SearchTerm ?? "", selected != null);
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
                UserId = userId, UserName = User.Identity?.Name ?? "User",
                FileName = fileName, ExportPath = path, RowCount = rows,
                GroupingMode = grouping, SearchSummary = summary,
                SelectedRowsOnly = selectedOnly, Status = "Completed", ExportTime = DateTime.Now
            });
            await _db.SaveChangesAsync();
        }

        // ── My Exports ──────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> MyExports()
        {
            var uid = await GetUserId();
            var exports = await _db.ExportLogs.AsNoTracking().Where(e => e.UserId == uid).OrderByDescending(e => e.ExportTime).ToListAsync();
            return View(new UserExportViewModel { Exports = exports.Select(e => new ExportItem { FileName = e.FileName, ExportTime = e.ExportTime.ToString("dd MMM yyyy HH:mm"), Status = e.Status }).ToList() });
        }

        [HttpGet]
        public IActionResult DownloadExport(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return RedirectToAction("MyExports");
            string fp = Path.Combine(_env.WebRootPath, "Exports", fileName);
            if (!System.IO.File.Exists(fp)) { TempData["SearchError"] = "File missing."; return RedirectToAction("MyExports"); }
            byte[] bytes = System.IO.File.ReadAllBytes(fp);
            return File(bytes, fileName.EndsWith(".csv") ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ── Snapshot persistence ────────────────────────────────────

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

        /// <summary>Validates that a file path is within allowed directories.</summary>
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

        private static string FmtAgo(DateTime dt)
        {
            var s = DateTime.Now - dt;
            if (s.TotalMinutes < 1) return "Just now";
            if (s.TotalMinutes < 60) return (int)s.TotalMinutes + " min ago";
            if (s.TotalHours < 24) return (int)s.TotalHours + " hr" + (s.TotalHours >= 2 ? "s" : "") + " ago";
            if (s.TotalDays < 7) return (int)s.TotalDays + " day" + (s.TotalDays >= 2 ? "s" : "") + " ago";
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
