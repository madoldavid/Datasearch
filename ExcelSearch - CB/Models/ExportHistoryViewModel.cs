using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class ExportHistoryViewModel : AdminBaseViewModel
    {
        public int TotalExports { get; set; }

        public int TodayExports { get; set; }

        public int WeeklyExports { get; set; }

        public string TotalSize { get; set; }

        public List<ExportHistoryItemViewModel> Exports { get; set; }
    }

    public class ExportHistoryItemViewModel
    {
        public string FileName { get; set; }

        public string ExportedBy { get; set; }

        public int Rows { get; set; }

        public string ExportTime { get; set; }

        public string Status { get; set; }
    }
}