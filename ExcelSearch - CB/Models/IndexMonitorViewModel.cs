using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class IndexMonitorViewModel : AdminBaseViewModel
    {
        //public string AdminName { get; set; }
        //public string AdminRole { get; set; }


        // KPI Cards
        public int QueueFiles { get; set; }
        public int ProcessingFiles { get; set; }
        public int CompletedFiles { get; set; }
        public int FailedFiles { get; set; }


        // Current Processing
        public string CurrentFile { get; set; }
        public string CurrentWorksheet { get; set; }

        public int RowsProcessed { get; set; }
        public int TotalRows { get; set; }

        public int CompletionPercentage { get; set; }

        public string EstimatedTime { get; set; }



        public List<IndexQueueItemViewModel> QueueItems { get; set; }


        public List<IndexActivityViewModel> Activities { get; set; }

    }



    public class IndexQueueItemViewModel
    {
        public string FileName { get; set; }

        public string Status { get; set; }

    }



    public class IndexActivityViewModel
    {
        public string Message { get; set; }

        public string Time { get; set; }
    }
}