using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ExcelSearch___CB.Models
{
  
        public class SearchHistoryViewModel : AdminBaseViewModel
        {
            public int TotalSearches { get; set; }

            public int TodaySearches { get; set; }

            public int AverageResults { get; set; }

            public string MostPopularSearch { get; set; }

            public List<SearchHistoryItemViewModel> Searches { get; set; }
        }

        public class SearchHistoryItemViewModel
        {
            public string UserName { get; set; }

            public string SearchTerm { get; set; }

            public string SearchMode { get; set; }

            public int Results { get; set; }

            public string SearchTime { get; set; }
        }
    }
