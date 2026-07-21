using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class ExportLog
    {
        public int Id { get; set; }

        [MaxLength(450)]
        public string UserId { get; set; }

        [MaxLength(200)]
        public string UserName { get; set; }

        [MaxLength(500)]
        public string FileName { get; set; }

        public string ExportPath { get; set; }

        public int RowCount { get; set; }

        /// <summary>BySourceFile, ByWorksheet, BySearchText, Flat</summary>
        [MaxLength(50)]
        public string GroupingMode { get; set; }

        /// <summary>Search filter summary that produced this export.</summary>
        [MaxLength(1000)]
        public string SearchSummary { get; set; }

        /// <summary>Whether only selected rows were exported.</summary>
        public bool SelectedRowsOnly { get; set; }

        [MaxLength(50)]
        public string Status { get; set; }

        public DateTime ExportTime { get; set; }
    }
}
