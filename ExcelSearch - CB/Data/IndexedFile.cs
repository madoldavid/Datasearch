using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class IndexedFile
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(500)]
        public string FileName { get; set; }

        [Required]
        public string FilePath { get; set; }

        public long FileSize { get; set; }

        public int RowCount { get; set; }

        public int WorksheetCount { get; set; }

        [MaxLength(500)]
        public string Worksheets { get; set; }

        [MaxLength(50)]
        public string Status { get; set; } // Uploaded, Indexed, Failed

        [MaxLength(1000)]
        public string ErrorMessage { get; set; }

        public DateTime UploadedAt { get; set; }

        public DateTime? LastIndexedAt { get; set; }

        public ICollection<IndexedRecord> Records { get; set; }
    }
}
