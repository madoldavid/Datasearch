using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class IndexedRecord
    {
        public long Id { get; set; }

        public int IndexedFileId { get; set; }

        [MaxLength(200)]
        public string WorksheetName { get; set; }

        public int RowNumber { get; set; }

        [MaxLength(500)]
        public string ColumnName { get; set; }

        public string ColumnValue { get; set; }

        public IndexedFile IndexedFile { get; set; }
    }
}
