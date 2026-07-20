using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    /// <summary>
    /// Persists user search results across app restarts.
    /// Each user gets one active search snapshot at a time.
    /// </summary>
    public class SearchSnapshot
    {
        [Key]
        [MaxLength(450)]
        public string UserId { get; set; }

        /// <summary>JSON-serialized SearchResultViewModel.</summary>
        public string ResultJson { get; set; }

        public DateTime SavedAt { get; set; }
    }
}
