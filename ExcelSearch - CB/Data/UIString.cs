using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class UIString
    {
        [Key]
        public int Id { get; set; }

        public string Key { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string Page { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
