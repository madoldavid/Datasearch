using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class SearchLog
    {
        public int Id { get; set; }

        [MaxLength(450)]
        public string UserId { get; set; }

        [MaxLength(200)]
        public string UserName { get; set; }

        [MaxLength(500)]
        public string SearchTerm { get; set; }

        [MaxLength(50)]
        public string SearchMode { get; set; }

        [MaxLength(200)]
        public string SearchColumn { get; set; }

        public int ResultCount { get; set; }

        public DateTime SearchTime { get; set; }
    }
}
