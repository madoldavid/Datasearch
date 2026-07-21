using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class DeletionLog
    {
        public int Id { get; set; }

        [MaxLength(450)]
        public string DeletedByUserId { get; set; }

        [MaxLength(200)]
        public string DeletedByUserName { get; set; }

        [MaxLength(500)]
        public string FileName { get; set; }

        public string FilePath { get; set; }

        public long FileSize { get; set; }

        public int RecordCount { get; set; }

        [MaxLength(1000)]
        public string Reason { get; set; }

        public DateTime DeletedAt { get; set; }
    }
}
