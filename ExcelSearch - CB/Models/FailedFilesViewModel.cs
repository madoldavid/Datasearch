using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class FailedFilesViewModel : AdminBaseViewModel
    {

        public int TotalFailedFiles { get; set; }

        public string Description { get; set; }

        public List<FailedFileItemViewModel> Files { get; set; }

    }



    public class FailedFileItemViewModel
    {

        public string FileName { get; set; }

        public string Reason { get; set; }

        public string FileSize { get; set; }

        public string LastAttempt { get; set; }

    }


}