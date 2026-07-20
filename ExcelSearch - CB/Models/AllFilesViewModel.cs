using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

    namespace ExcelSearch___CB.Models
    {
        public class AllFilesViewModel : AdminBaseViewModel
    {
            

            public int TotalFiles { get; set; }

            public string SummaryText { get; set; }

            public List<FileItemViewModel> Files { get; set; }
        }


        public class FileItemViewModel
        {
            public string FileName { get; set; }

            public string FileSize { get; set; }

            public string LastIndexed { get; set; }

            public long Records { get; set; }

            public string Status { get; set; }
        }
    }
