using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
    public class SearchResultViewModel
    {
        public string SearchTerm { get; set; }

        public int TotalMatches { get; set; }

        public List<SearchFileResult> Files { get; set; }

        /// <summary>Skipped sheet names due to missing fields in multi-condition filter.</summary>
        public List<string> SkippedSheets { get; set; }

        // Pagination
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalFiles { get; set; }
        public int TotalPages { get; set; }

        public SearchResultViewModel()
        {
            Files = new List<SearchFileResult>();
            SkippedSheets = new List<string>();
        }
    }

    public class SearchFileResult
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string Worksheet { get; set; }
        public int MatchCount { get; set; }
        public string SampleValue { get; set; }
        /// <summary>Which column had the match.</summary>
        public string MatchedColumn { get; set; }
        public string LastIndexed { get; set; }
        public List<Dictionary<string, string>> Rows { get; set; }

        public SearchFileResult()
        {
            Rows = new List<Dictionary<string, string>>();
        }
    }

}
    
