using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class OverviewViewModel : AdminBaseViewModel
    {

            //public string AdminName { get; set; }
            //public string AdminRole { get; set; }


            public int FilesIndexed { get; set; }
            public string FilesIndexedDescription { get; set; }


            public long TotalRecords { get; set; }
            public string TotalRecordsDescription { get; set; }


            public int FailedFiles { get; set; }
            public string FailedFilesDescription { get; set; }


            public decimal StorageUsed { get; set; }
            public decimal StorageLimit { get; set; }


            public DateTime LastIndexTime { get; set; }


            public List<PipelineStageViewModel> PipelineStages { get; set; }
        }


        public class PipelineStageViewModel
        {
            public string StageName { get; set; }
            public string Status { get; set; }
            public string Details { get; set; }
        }
    }
