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

        [MaxLength(2000)]
        public string Worksheets { get; set; }

        [MaxLength(50)]
        public string Status { get; set; } // Uploaded, Indexing, Indexed, Failed

        [MaxLength(2000)]
        public string ErrorMessage { get; set; }

        /// <summary>SHA256 hash of file content for duplicate/change detection.</summary>
        [MaxLength(64)]
        public string FileHash { get; set; }

        /// <summary>Folder this file was discovered in (upload or configured scan folder).</summary>
        [MaxLength(1000)]
        public string SourceFolder { get; set; }

        public DateTime UploadedAt { get; set; }

        public DateTime? LastIndexedAt { get; set; }

        public ICollection<IndexedRecord> Records { get; set; }
    }
}
