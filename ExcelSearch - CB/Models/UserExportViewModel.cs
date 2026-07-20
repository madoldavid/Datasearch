using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class UserExportViewModel
    {
            public List<ExportItem> Exports { get; set; }


            public UserExportViewModel()
            {
                Exports = new List<ExportItem>();
            }
        }


        public class ExportItem
        {
            public string FileName { get; set; }

            public string ExportTime { get; set; }

            public string Status { get; set; }
        }
    }
