using System.Collections.Generic;

namespace ExcelSearch___CB.Models
{
    public class IndexingHistoryViewModel : AdminBaseViewModel
    {
        public int TotalRuns { get; set; }
        public int SuccessfulRuns { get; set; }
        public int FailedRuns { get; set; }
        public long TotalRowsIndexed { get; set; }
        public List<IndexingRunViewModel> Runs { get; set; }
    }

    public class IndexingRunViewModel
    {
        public string FileName { get; set; }
        public string FileSize { get; set; }
        public int RowCount { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
        public string StartedAt { get; set; }
        public string CompletedAt { get; set; }
        public string Duration { get; set; }
    }
}
