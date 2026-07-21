using ExcelDataReader;
using ExcelSearch___CB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ExcelSearch___CB.Services
{
    public class FileIndexingService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger<FileIndexingService> _logger;
        private const int FlushInterval = 2_000;

        public FileIndexingService(IDbContextFactory<AppDbContext> dbFactory,
            ILogger<FileIndexingService> logger)
        { _dbFactory = dbFactory; _logger = logger; }

        // ── Hash computation ──────────────────────────────────────────

        public static string ComputeFileHash(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // ── Duplicate detection result ────────────────────────────────

        public class DuplicateInfo
        {
            public bool Exists { get; set; }
            public IndexedFile ExistingFile { get; set; }
            public bool SameHash { get; set; }
            public string IncomingHash { get; set; }
            public string ExistingHash { get; set; }
            public long IncomingSize { get; set; }
            public long ExistingSize { get; set; }
            public string IncomingName { get; set; }
            public string ExistingName { get; set; }
        }

        public DuplicateInfo CheckDuplicate(string filePath, string originalFileName)
        {
            var fi = new FileInfo(filePath);
            var incomingHash = ComputeFileHash(filePath);

            using var db = _dbFactory.CreateDbContext();
            // Check by same file path first
            var existing = db.IndexedFiles.AsNoTracking()
                .FirstOrDefault(f => f.FilePath == filePath);

            if (existing == null)
            {
                // Check by filename
                existing = db.IndexedFiles.AsNoTracking()
                    .FirstOrDefault(f => f.FileName == originalFileName);
            }

            if (existing == null)
                return new DuplicateInfo { Exists = false, IncomingHash = incomingHash,
                    IncomingSize = fi.Length, IncomingName = originalFileName };

            return new DuplicateInfo
            {
                Exists = true,
                ExistingFile = existing,
                SameHash = existing.FileHash == incomingHash,
                IncomingHash = incomingHash,
                ExistingHash = existing.FileHash ?? "(not computed)",
                IncomingSize = fi.Length,
                ExistingSize = existing.FileSize,
                IncomingName = originalFileName,
                ExistingName = existing.FileName
            };
        }

        // ── Public indexing API ───────────────────────────────────────

        public IndexedFile IndexFile(string filePath, string originalFileName,
            string sourceFolder = null)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            if (extension != ".xlsx" && extension != ".xls" && extension != ".csv")
                throw new InvalidOperationException("Unsupported format: " + extension);

            var fi = new FileInfo(filePath);
            string hash = ComputeFileHash(filePath);

            _logger.LogInformation("Indexing {File} ({Size} bytes, hash={Hash})",
                originalFileName, fi.Length, hash);

            // Save reference to old records so we can restore if indexing fails
            List<IndexedRecord> oldRecordsBackup = null;
            IndexedFile oldFileEntry = null;

            using (var db = _dbFactory.CreateDbContext())
            {
                oldFileEntry = db.IndexedFiles.AsNoTracking()
                    .FirstOrDefault(f => f.FilePath == filePath);
            }

            // Create the new file entry FIRST (before deleting old data)
            var indexedFile = new IndexedFile
            {
                FileName = originalFileName,
                FilePath = filePath,
                FileSize = fi.Length,
                FileHash = hash,
                SourceFolder = sourceFolder ?? Path.GetDirectoryName(filePath),
                Status = "Indexing",
                UploadedAt = DateTime.Now,
                LastIndexedAt = DateTime.Now,
                RowCount = 0, WorksheetCount = 0, Worksheets = ""
            };

            using (var db = _dbFactory.CreateDbContext())
            { db.IndexedFiles.Add(indexedFile); db.SaveChanges(); }

            int fileId = indexedFile.Id;

            try
            {
                // Index the new data first
                if (extension == ".csv") ParseCSV(filePath, fileId);
                else ParseExcel(filePath, fileId);

                // SUCCESS — now safe to delete old records
                if (oldFileEntry != null)
                {
                    using var db = _dbFactory.CreateDbContext();
                    ExecuteWithRetry(db, () =>
                        db.Database.ExecuteSqlRaw(
                            "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", oldFileEntry.Id));
                    var toRemove = db.IndexedFiles.Find(oldFileEntry.Id);
                    if (toRemove != null) { db.IndexedFiles.Remove(toRemove); db.SaveChanges(); }
                }

                // Also clean up same-name files at different paths
                using (var db2 = _dbFactory.CreateDbContext())
                {
                    var sameNames = db2.IndexedFiles
                        .Where(f => f.FileName == originalFileName && f.FilePath != filePath && f.Id != fileId)
                        .ToList();
                    foreach (var old in sameNames)
                    {
                        ExecuteWithRetry(db2, () =>
                            db2.Database.ExecuteSqlRaw(
                                "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", old.Id));
                        db2.IndexedFiles.Remove(old);
                    }
                    if (sameNames.Count > 0) db2.SaveChanges();
                }

                // Mark as Indexed
                using (var db3 = _dbFactory.CreateDbContext())
                {
                    var file = db3.IndexedFiles.Find(fileId);
                    if (file != null)
                    {
                        file.Status = "Indexed";
                        file.WorksheetCount = (file.Worksheets ?? "")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
                        file.LastIndexedAt = DateTime.Now;
                        file.FileHash = hash;
                        db3.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing FAILED {File} (id={Id})", originalFileName, fileId);

                // FAILURE — remove the failed new entry, old data is still intact
                using (var db = _dbFactory.CreateDbContext())
                {
                    var failed = db.IndexedFiles.Find(fileId);
                    if (failed != null)
                    {
                        // Remove any partial records
                        ExecuteWithRetry(db, () =>
                            db.Database.ExecuteSqlRaw(
                                "DELETE FROM IndexedRecords WHERE IndexedFileId = {0}", fileId));
                        // Replace with a Failed status marker
                        failed.Status = "Failed";
                        failed.ErrorMessage = DetectErrorReason(ex, extension);
                        failed.RowCount = 0;
                        db.SaveChanges();
                    }
                }
            }

            using var dbFinal = _dbFactory.CreateDbContext();
            return dbFinal.IndexedFiles.AsNoTracking().First(f => f.Id == fileId);
        }

        /// <summary>Smart error detection for password-protected and corrupt files.</summary>
        private static string DetectErrorReason(Exception ex, string extension)
        {
            var msg = ex.Message.ToLower();
            if (msg.Contains("password") || msg.Contains("protected") || msg.Contains("encrypted"))
                return "File is password-protected or encrypted";
            if (msg.Contains("corrupt") || msg.Contains("invalid") || msg.Contains("format"))
                return "File appears to be corrupted or has an invalid format";
            if (ex.InnerException != null)
                return DetectErrorReason(ex.InnerException, extension);
            return ex.Message;
        }

        /// <summary>Fast skip-check: is this file already indexed with current hash?</summary>
        public bool IsAlreadyIndexed(string filePath)
        {
            string hash;
            try { hash = ComputeFileHash(filePath); }
            catch { return false; }

            using var db = _dbFactory.CreateDbContext();
            return db.IndexedFiles.AsNoTracking()
                .Any(f => f.FilePath == filePath && f.FileHash == hash && f.Status == "Indexed");
        }

        // ── Folder scanning ───────────────────────────────────────────

        public List<string> ScanFolderForNewFiles(string folderPath)
        {
            var results = new List<string>();
            if (!Directory.Exists(folderPath)) return results;

            var supported = new[] { ".xlsx", ".xls", ".csv" };
            foreach (var file in Directory.GetFiles(folderPath, "*.*",
                SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLower();
                if (!supported.Contains(ext)) continue;
                if (IsAlreadyIndexed(file)) continue;
                results.Add(file);
            }
            return results;
        }

        // ── Excel parsing ─────────────────────────────────────────────

        private void ParseExcel(string filePath, int fileId)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int totalRows = 0;
            var sheetNames = new List<string>();
            var batch = new List<IndexedRecord>(FlushInterval);

            using var stream = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 65536);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                string ws = reader.Name ?? "Sheet" + (sheetNames.Count + 1);
                sheetNames.Add(ws);
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
                            if (string.IsNullOrWhiteSpace(h)) h = "Column" + (i + 1);
                            headers.Add(h);
                        }
                        continue;
                    }

                    for (int i = 0; i < reader.FieldCount; i++)
                        batch.Add(new IndexedRecord
                        {
                            IndexedFileId = fileId, WorksheetName = ws,
                            RowNumber = rowNum + 1,
                            ColumnName = headers[i],
                            ColumnValue = reader.GetValue(i)?.ToString() ?? ""
                        });

                    rowNum++; totalRows++;
                    if (batch.Count >= FlushInterval) { FlushBatch(fileId, batch); batch.Clear(); }
                }
            }
            while (reader.NextResult());

            if (batch.Count > 0) FlushBatch(fileId, batch);
            UpdateFileMeta(fileId, string.Join(",", sheetNames), totalRows);
        }

        // ── CSV parsing ───────────────────────────────────────────────

        private void ParseCSV(string filePath, int fileId)
        {
            int totalRows = 0;
            var batch = new List<IndexedRecord>(FlushInterval);
            string[] headers = null;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536);
            using var sr = new StreamReader(fs, Encoding.UTF8, true, 65536);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var values = SplitCsvLine(line);

                if (headers == null) { headers = values; continue; }

                for (int c = 0; c < headers.Length; c++)
                    batch.Add(new IndexedRecord
                    {
                        IndexedFileId = fileId, WorksheetName = "CSV Data",
                        RowNumber = totalRows + 1,
                        ColumnName = headers[c],
                        ColumnValue = c < values.Length ? (values[c] ?? "") : ""
                    });

                totalRows++;
                if (batch.Count >= FlushInterval) { FlushBatch(fileId, batch); batch.Clear(); }
            }

            if (batch.Count > 0) FlushBatch(fileId, batch);
            UpdateFileMeta(fileId, "CSV Data", totalRows);
        }

        private static string[] SplitCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var cur = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    { cur.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (ch == ',' && !inQuotes)
                { result.Add(cur.ToString().Trim()); cur.Clear(); }
                else cur.Append(ch);
            }
            result.Add(cur.ToString().Trim());
            return result.ToArray();
        }

        // ── Batch flush ───────────────────────────────────────────────

        private void FlushBatch(int fileId, List<IndexedRecord> batch)
        {
            if (batch.Count == 0) return;
            using var db = _dbFactory.CreateDbContext();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            ExecuteWithRetry(db, () => { db.IndexedRecords.AddRange(batch); db.SaveChanges(); });
        }

        private void UpdateFileMeta(int fileId, string worksheets, int rowCount)
        {
            using var db = _dbFactory.CreateDbContext();
            var f = db.IndexedFiles.Find(fileId);
            if (f != null) { f.Worksheets = worksheets; f.RowCount = rowCount; db.SaveChanges(); }
        }

        // ── Retry ─────────────────────────────────────────────────────

        private static void ExecuteWithRetry(AppDbContext db, Action action, int max = 5)
        {
            for (int i = 0; i < max; i++)
                try { action(); return; }
                catch (Exception ex) when (IsRetryable(ex) && i < max - 1)
                { Thread.Sleep((int)Math.Pow(2, i) * 50); }
        }

        private static bool IsRetryable(Exception ex)
        {
            var m = ex.Message.ToLower();
            return m.Contains("database is locked") || m.Contains("busy")
                || m.Contains("disk i/o error");
        }

        // ── Preview ───────────────────────────────────────────────────

        public (List<Dictionary<string, string>> Rows, List<string> Headers)
            PreviewFile(string filePath, int maxRows = 500)
        {
            var ext = Path.GetExtension(filePath).ToLower();
            return ext == ".csv" ? PreviewCSV(filePath, maxRows)
                : PreviewExcel(filePath, maxRows);
        }

        private (List<Dictionary<string, string>>, List<string>)
            PreviewExcel(string path, int maxRows)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var rows = new List<Dictionary<string, string>>(maxRows);
            List<string> headers = null;
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536);
            using var r = ExcelReaderFactory.CreateReader(s);
            while (r.Read())
            {
                if (headers == null)
                {
                    headers = new List<string>(r.FieldCount);
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        string h = r.GetValue(i)?.ToString();
                        if (string.IsNullOrWhiteSpace(h)) h = "Column" + (i + 1);
                        headers.Add(h);
                    }
                    continue;
                }
                var row = new Dictionary<string, string>(r.FieldCount);
                for (int i = 0; i < r.FieldCount; i++)
                    row[headers[i]] = r.GetValue(i)?.ToString() ?? "";
                rows.Add(row);
                if (rows.Count >= maxRows) break;
            }
            return (rows, headers ?? new List<string>());
        }

        private (List<Dictionary<string, string>>, List<string>)
            PreviewCSV(string path, int maxRows)
        {
            var rows = new List<Dictionary<string, string>>(maxRows);
            List<string> headers = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536);
            using var sr = new StreamReader(fs, Encoding.UTF8, true, 65536);
            string line;
            while ((line = sr.ReadLine()) != null && rows.Count < maxRows)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var v = SplitCsvLine(line);
                if (headers == null) { headers = v.ToList(); continue; }
                var row = new Dictionary<string, string>(Math.Min(headers.Count, v.Length));
                for (int c = 0; c < headers.Count; c++)
                    row[headers[c]] = c < v.Length ? (v[c] ?? "") : "";
                rows.Add(row);
            }
            return (rows, headers ?? new List<string>());
        }
    }
}
