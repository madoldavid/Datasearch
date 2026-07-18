using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class UploadViewModel : AdminBaseViewModel
    {
        
            //public string AdminName { get; set; }
            //public string AdminRole { get; set; }


            public List<UploadedFileViewModel> RecentUploads { get; set; }
        }


        public class UploadedFileViewModel
        {
            public string FileName { get; set; }

            public string FileSize { get; set; }

            public long RowCount { get; set; }

            public string UploadedTime { get; set; }

            public string Status { get; set; }
        }
    }
