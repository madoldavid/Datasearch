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

        [MaxLength(50)]
        public string Status { get; set; }

        public DateTime ExportTime { get; set; }
    }
}
