namespace ExcelSearch___CB.Data
{
    /// <summary>
    /// Application-wide named constants. All hardcoded strings
    /// used in business logic live here so nothing is buried in code.
    /// </summary>
    public static class AppConstants
    {
        // ── Search mode names ──────────────────────────────────────
        public const string SearchModePartial  = "Partial";
        public const string SearchModeExact    = "Exact";
        public const string SearchModeFlexible = "Flexible";
        public const string SearchModeFilter   = "Filter";

        // ── Column sentinel ────────────────────────────────────────
        public const string AllColumns = "All";

        // ── File statuses ──────────────────────────────────────────
        public const string StatusIndexed   = "Indexed";
        public const string StatusIndexing  = "Indexing";
        public const string StatusFailed    = "Failed";
        public const string StatusUploaded  = "Uploaded";

        // ── Export status ──────────────────────────────────────────
        public const string ExportCompleted = "Completed";

        // ── Default worksheet name ─────────────────────────────────
        public const string DefaultWorksheet = "Data";

        // ── CSV worksheet name ─────────────────────────────────────
        public const string CsvWorksheet = "CSV Data";

        // ── Export grouping modes ──────────────────────────────────
        public const string GroupingFlat        = "flat";
        public const string GroupingByFile      = "byfile";
        public const string GroupingByWorksheet = "byworksheet";
        public const string GroupingBySearch    = "bysearchtext";

        // ── Export formats ─────────────────────────────────────────
        public const string ExportFormatXlsx = "xlsx";
        public const string ExportFormatCsv  = "csv";

        // ── Preview & pagination limits ────────────────────────────
        public const int PreviewMaxRows     = 500;
        public const int SearchPageSize     = 25;
        public const int ResultRowGroupMax  = 50;
        public const int HistoryRecordLimit = 500;
        public const int IndexingRunLimit   = 200;
        public const int FlushInterval      = 2000;

        // ── Storage defaults ───────────────────────────────────────
        public const double StorageLimitGB  = 50;
        public const int    StoragePercentMax = 100;
    }
}
