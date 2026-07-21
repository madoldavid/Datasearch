using System;
using System.ComponentModel.DataAnnotations;

namespace ExcelSearch___CB.Data
{
    public class AppConfig
    {
        [Key]
        public int Id { get; set; }

        public string AppName { get; set; } = "Excel Search Pro";

        public string CompanyName { get; set; } = "Coldwell Banker Real Estate";

        public string AppDescription { get; set; } = "Search, manage and export real estate records efficiently.";

        public string PlatformType { get; set; } = "Real Estate Data Platform";

        public string BrandMark { get; set; } = "CB";

        public string BrandIcon { get; set; } = "ESP";

        public string FooterText { get; set; } = "Data Workspace";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
