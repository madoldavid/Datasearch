using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    
     public class StorageViewModel : AdminBaseViewModel
        {
            public string StorageUsed { get; set; }

            public string StorageLimit { get; set; }

            public int StoragePercentage { get; set; }

            public int TotalFiles { get; set; }

            public string DatabaseSize { get; set; }

            public string TotalRecords { get; set; }

            public string LastBackup { get; set; }

            public List<StorageFileViewModel> Files { get; set; }
        }


        public class StorageFileViewModel
        {
            public string FileName { get; set; }

            public string FileSize { get; set; }
        }
    }
