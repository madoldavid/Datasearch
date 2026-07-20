using ExcelDataReader;
using ExcelSearch___CB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ExcelSearch___CB.Services
{
    public class FileIndexingService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger<FileIndexingService> _logger;

        // Flush to DB every N records. Tuned for throughput without
        // letting the in-memory batch balloon with huge files.
        private const int FlushInterval = 2_000;

        public FileIndexingService(
            IDbContextFactory<AppDbContext> dbFactory,
            ILogger<FileIndexingService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>
        /// Index an Excel or CSV file. Replaces any previously-indexed
        /// copy of the same path. Only bottleneck is available disk space —
        /// no artificial file size limit.
        /// </summary>
        public IndexedFile IndexFile(string filePath, string originalFileName)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            if (extension != ".xlsx" && extension != ".xls" && extension != ".csv")
                throw new InvalidOperationException("Unsupported format: " + extension);

            var fi = new FileInfo(filePath);
            _logger.LogInformation("Indexing {File} ({Size} bytes)", originalFileName, fi.Length);

            // Remove any previous version of this file's data.
            using (var db = _dbFactory.CreateDbContext())
            {
                var existing = db.IndexedFiles.FirstOrDefault(f => f.FilePath == filePath);
                if (existing != null)
                {
                    ExecuteWithRetry(db, () =>
                        db.Database.ExecuteSqlRaw(
                            "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", existing.Id));
                    db.IndexedFiles.Remove(existing);
                    db.SaveChanges();
                }
            }

            var indexedFile = new IndexedFile
            {
                FileName = originalFileName,
                FilePath = filePath,
                FileSize = fi.Length,
                Status = "Indexing",
                UploadedAt = DateTime.Now,
                LastIndexedAt = DateTime.Now,
                RowCount = 0,
                WorksheetCount = 0,
                Worksheets = ""
            };

            using (var db = _dbFactory.CreateDbContext())
            {
                db.IndexedFiles.Add(indexedFile);
                db.SaveChanges();
            }

            int fileId = indexedFile.Id;

            try
            {
                if (extension == ".csv")
                    ParseCSV(filePath, fileId);
                else
                    ParseExcel(filePath, fileId);

                using (var db = _dbFactory.CreateDbContext())
                {
                    var file = db.IndexedFiles.Find(fileId);
                    if (file != null)
                    {
                        file.Status = "Indexed";
                        file.WorksheetCount = (file.Worksheets ?? "")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
                        file.LastIndexedAt = DateTime.Now;
                        db.SaveChanges();
                    }
                }

                _logger.LogInformation("Indexed {File}: {Rows} rows", originalFileName,
                    GetRowCount(fileId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing FAILED for {File} (id={Id})", originalFileName, fileId);

                using (var db = _dbFactory.CreateDbContext())
                {
                    var file = db.IndexedFiles.Find(fileId);
                    if (file != null)
                    {
                        file.Status = "Failed";
                        file.ErrorMessage = ex.Message;
                        db.SaveChanges();
                    }
                }
                // Don't rethrow — file is marked Failed, admin can retry.
            }

            using (var db = _dbFactory.CreateDbContext())
                return db.IndexedFiles.AsNoTracking().First(f => f.Id == fileId);
        }

        // ── Excel (streaming via ExcelDataReader) ───────────────────────

        private void ParseExcel(string filePath, int fileId)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            int totalRows = 0;
            var worksheetNames = new List<string>();
            var batch = new List<IndexedRecord>(FlushInterval);

            using var stream = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);

            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                string wsName = reader.Name ?? "Sheet" + (worksheetNames.Count + 1);
                worksheetNames.Add(wsName);

                List<string> headers = null;
                int rowNum = 0;

                while (reader.Read())
                {
                    if (headers == null)
                    {
                        headers = new List<string>(reader.FieldCount);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string h = reader.GetValue(i)?.ToString();
                            if (string.IsNullOrWhiteSpace(h))
                                h = "Column" + (i + 1);
                            headers.Add(h);
                        }
                        continue;
                    }

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        batch.Add(new IndexedRecord
                        {
                            IndexedFileId = fileId,
                            WorksheetName = wsName,
                            RowNumber = rowNum + 1,
                            ColumnName = headers[i],
                            ColumnValue = reader.GetValue(i)?.ToString() ?? ""
                        });
                    }

                    rowNum++;
                    totalRows++;

                    if (batch.Count >= FlushInterval)
                    {
                        FlushBatch(fileId, batch);
                        batch.Clear();
                    }
                }
            }
            while (reader.NextResult());

            if (batch.Count > 0)
                FlushBatch(fileId, batch);

            UpdateFileMeta(fileId, string.Join(",", worksheetNames), totalRows);
        }

        // ── CSV (streaming, respects quoted fields) ─────────────────────

        private void ParseCSV(string filePath, int fileId)
        {
            int totalRows = 0;
            var batch = new List<IndexedRecord>(FlushInterval);
            string[] headers = null;

            using var fs = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var sr = new StreamReader(fs, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var values = SplitCsvLine(line);

                if (headers == null)
                {
                    headers = values;
                    continue;
                }

                for (int c = 0; c < headers.Length; c++)
                {
                    batch.Add(new IndexedRecord
                    {
                        IndexedFileId = fileId,
                        WorksheetName = "CSV Data",
                        RowNumber = totalRows + 1,
                        ColumnName = headers[c],
                        ColumnValue = c < values.Length ? (values[c] ?? "") : ""
                    });
                }

                totalRows++;

                if (batch.Count >= FlushInterval)
                {
                    FlushBatch(fileId, batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                FlushBatch(fileId, batch);

            UpdateFileMeta(fileId, "CSV Data", totalRows);
        }

        // ── RFC 4180-aware CSV splitter ─────────────────────────────────

        private static string[] SplitCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    { current.Append('"'); i++; }
                    else
                    { inQuotes = !inQuotes; }
                }
                else if (ch == ',' && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }
            result.Add(current.ToString().Trim());
            return result.ToArray();
        }

        // ── Batch flush with retry for SQLite busy/locked ───────────────

        private void FlushBatch(int fileId, List<IndexedRecord> batch)
        {
            if (batch.Count == 0) return;

            using var db = _dbFactory.CreateDbContext();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            ExecuteWithRetry(db, () =>
            {
                db.IndexedRecords.AddRange(batch);
                db.SaveChanges();
            });

            _logger.LogDebug("Flushed {Count} records for file id={Id}", batch.Count, fileId);
        }

        private void UpdateFileMeta(int fileId, string worksheets, int rowCount)
        {
            using var db = _dbFactory.CreateDbContext();
            var file = db.IndexedFiles.Find(fileId);
            if (file != null)
            {
                file.Worksheets = worksheets;
                file.RowCount = rowCount;
                db.SaveChanges();
            }
        }

        private int GetRowCount(int fileId)
        {
            using var db = _dbFactory.CreateDbContext();
            return db.IndexedFiles.AsNoTracking()
                .Where(f => f.Id == fileId).Select(f => f.RowCount).FirstOrDefault();
        }

        // ── SQLite retry with exponential backoff ────────────────────────

        private static void ExecuteWithRetry(AppDbContext db, Action action,
            int maxRetries = 5)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (IsRetryable(ex) && attempt < maxRetries - 1)
                {
                    int delay = (int)Math.Pow(2, attempt) * 50; // 50, 100, 200, 400, 800 ms
                    Thread.Sleep(delay);
                }
            }
        }

        private static bool IsRetryable(Exception ex)
        {
            var msg = ex.Message.ToLower();
            return msg.Contains("database is locked")
                || msg.Contains("busy")
                || msg.Contains("disk I/O error");
        }

        // ── Preview (streaming, read-only) ──────────────────────────────

        public (List<Dictionary<string, string>> Rows, List<string> Headers)
            PreviewFile(string filePath, int maxRows = 500)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            return ext == ".csv"
                ? PreviewCSV(filePath, maxRows)
                : PreviewExcel(filePath, maxRows);
        }

        private (List<Dictionary<string, string>>, List<string>)
            PreviewExcel(string filePath, int maxRows)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var rows = new List<Dictionary<string, string>>(maxRows);
            List<string> headers = null;

            using var stream = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            while (reader.Read())
            {
                if (headers == null)
                {
                    headers = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string h = reader.GetValue(i)?.ToString();
                        if (string.IsNullOrWhiteSpace(h)) h = "Column" + (i + 1);
                        headers.Add(h);
                    }
                    continue;
                }

                var row = new Dictionary<string, string>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[headers[i]] = reader.GetValue(i)?.ToString() ?? "";
                rows.Add(row);
                if (rows.Count >= maxRows) break;
            }

            return (rows, headers ?? new List<string>());
        }

        private (List<Dictionary<string, string>>, List<string>)
            PreviewCSV(string filePath, int maxRows)
        {
            var rows = new List<Dictionary<string, string>>(maxRows);
            List<string> headers = null;

            using var fs = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var sr = new StreamReader(fs, Encoding.UTF8, true, 65536);

            string line;
            while ((line = sr.ReadLine()) != null && rows.Count < maxRows)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var values = SplitCsvLine(line);

                if (headers == null)
                { headers = values.ToList(); continue; }

                var row = new Dictionary<string, string>(
                    Math.Min(headers.Count, values.Length));
                for (int c = 0; c < headers.Count; c++)
                    row[headers[c]] = c < values.Length ? (values[c] ?? "") : "";
                rows.Add(row);
            }

            return (rows, headers ?? new List<string>());
        }
    }
}
