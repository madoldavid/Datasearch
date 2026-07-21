using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExcelSearch___CB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ExcelSearch___CB.Services
{
    public class ConfigurationService
    {
        private readonly AppDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(10);

        public ConfigurationService(AppDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<AppConfig> GetAppConfig()
        {
            string cacheKey = "AppConfig";
            
            if (_cache.TryGetValue(cacheKey, out AppConfig? cached))
            {
                return cached!;
            }

            var config = await _db.AppConfigs.FirstOrDefaultAsync();
            
            if (config == null)
            {
                config = new AppConfig
                {
                    AppName = "Excel Search Pro",
                    CompanyName = "Coldwell Banker Real Estate",
                    AppDescription = "Search, manage and export real estate records efficiently.",
                    PlatformType = "Real Estate Data Platform",
                    BrandMark = "CB",
                    BrandIcon = "ESP",
                    FooterText = "Data Workspace"
                };
                _db.AppConfigs.Add(config);
                await _db.SaveChangesAsync();
            }

            _cache.Set(cacheKey, config, _cacheDuration);
            return config;
        }

        public async Task<string> GetString(string key, string defaultValue = "")
        {
            string cacheKey = $"UIString_{key}";
            
            if (_cache.TryGetValue(cacheKey, out string? cached))
            {
                return cached ?? defaultValue;
            }

            var uiString = await _db.UIStrings.FirstOrDefaultAsync(s => s.Key == key);
            var value = uiString?.Value ?? defaultValue;

            _cache.Set(cacheKey, value, _cacheDuration);
            return value;
        }

        public async Task<Dictionary<string, string>> GetStringsByCategory(string category)
        {
            string cacheKey = $"UIStrings_Category_{category}";
            
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached))
            {
                return cached!;
            }

            var strings = await _db.UIStrings
                .Where(s => s.Category == category)
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            _cache.Set(cacheKey, strings, _cacheDuration);
            return strings;
        }

        public async Task<Dictionary<string, string>> GetStringsByPage(string page)
        {
            string cacheKey = $"UIStrings_Page_{page}";
            
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached))
            {
                return cached!;
            }

            var strings = await _db.UIStrings
                .Where(s => s.Page == page)
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            _cache.Set(cacheKey, strings, _cacheDuration);
            return strings;
        }

        public async Task ClearCache()
        {
            // In a real application, you might want more granular cache clearing
            // For now, this is a simple implementation
        }
    }
}
