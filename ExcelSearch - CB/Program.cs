using ExcelSearch___CB.Data;
using ExcelSearch___CB.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Required for ExcelDataReader to handle legacy code pages in older .xls files.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// ── Paths ────────────────────────────────────────────────────────────────
// Resolve paths relative to the project directory (where the .csproj lives),
// NOT the current working directory. This means the app works regardless of
// where you run `dotnet run` from.

var contentRoot = builder.Environment.ContentRootPath;

var dbPath = Path.Combine(contentRoot, "App_Data", "ExcelSearch.db");
var dbDir = Path.GetDirectoryName(dbPath)!;
if (!Directory.Exists(dbDir)) Directory.CreateDirectory(dbDir);

// ── Configuration with sensible defaults ────────────────────────────────

// No artificial file size limit — 0 means unbounded.
var maxFileSizeMB = builder.Configuration.GetValue<long>("Storage:MaxFileSizeMB", 0);
long? maxFileSizeBytes = maxFileSizeMB > 0 ? maxFileSizeMB * 1024L * 1024L : null;

// ── Database ─────────────────────────────────────────────────────────────

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}", sqliteOptions =>
    {
        sqliteOptions.CommandTimeout(300); // 5 min for massive batch inserts
    });
    options.EnableSensitiveDataLogging(false);
    options.EnableDetailedErrors(builder.Environment.IsDevelopment());
});

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}", sqliteOptions =>
    {
        sqliteOptions.CommandTimeout(300);
    });
}, ServiceLifetime.Scoped);

// ── Identity ─────────────────────────────────────────────────────────────

builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.User.RequireUniqueEmail = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Home/Login";
    options.AccessDeniedPath = "/Home/Login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// ── MVC ──────────────────────────────────────────────────────────────────

builder.Services.AddControllersWithViews();

// ── Session (in-memory for transient state; search results persisted to DB) ──

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ── Application Services ─────────────────────────────────────────────────

builder.Services.AddScoped<FileIndexingService>();
builder.Services.AddScoped<ConfigurationService>();

// ── Upload Limits ────────────────────────────────────────────────────────

builder.Services.Configure<FormOptions>(options =>
{
    if (maxFileSizeBytes.HasValue)
        options.MultipartBodyLengthLimit = maxFileSizeBytes.Value;
    // else: ASP.NET Core default (approx 128 MB per form).
    // We also set Kestrel's limit below which is the true gate.
    options.MemoryBufferThreshold = 1 * 1024 * 1024;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(options =>
{
    // If no limit configured, allow up to long.MaxValue (~9 EB — effectively unbounded).
    options.Limits.MaxRequestBodySize = maxFileSizeBytes ?? long.MaxValue;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
});

// ── Logging ──────────────────────────────────────────────────────────────

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(
    builder.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Warning);

// ═════════════════════════════════════════════════════════════════════════
// BUILD
// ═════════════════════════════════════════════════════════════════════════

var app = builder.Build();

// ── Database initialisation ──────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await db.Database.EnsureCreatedAsync();

    // ── SQLite pragmas for production-grade durability ──────────────────

    // WAL mode: concurrent reads + writes without blocking.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");

    // NORMAL sync: safe across OS crash, much faster than FULL.
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;");

    // 64 MB page cache.
    await db.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -65536;");

    // Store temp tables/indexes in memory.
    await db.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;");

    // 256 MB memory-mapped I/O.
    await db.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 268435456;");

    // Wait up to 30 seconds before giving up on a locked database.
    await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 30000;");

    // Enable foreign keys.
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

    // ── Integrity check on startup ──────────────────────────────────────

    try
    {
        var integrity = await db.Database.ExecuteSqlRawAsync("PRAGMA integrity_check;");
        logger.LogInformation("Database integrity check passed");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Database integrity check FAILED — database may be corrupt");
    }

    // ── Seed roles and users ────────────────────────────────────────────

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    var adminRole = app.Configuration.GetValue("RoleNames:Admin", "Admin")!;
    var userRole = app.Configuration.GetValue("RoleNames:User", "User")!;

    if (!await roleManager.RoleExistsAsync(adminRole))
        await roleManager.CreateAsync(new IdentityRole(adminRole));
    if (!await roleManager.RoleExistsAsync(userRole))
        await roleManager.CreateAsync(new IdentityRole(userRole));

    var adminCfg = app.Configuration.GetSection("SeedUsers:Admin");
    var adminUser = await userManager.FindByNameAsync(
        adminCfg.GetValue("UserName", "admin")!);
    if (adminUser == null)
    {
        adminUser = new AppUser
        {
            UserName = adminCfg.GetValue("UserName", "admin")!,
            FullName = adminCfg.GetValue("FullName", "Administrator")
        };
        await userManager.CreateAsync(adminUser,
            adminCfg.GetValue("Password", "admin123")!);
    }
    if (!await userManager.IsInRoleAsync(adminUser, adminRole))
        await userManager.AddToRoleAsync(adminUser, adminRole);

    var userCfg = app.Configuration.GetSection("SeedUsers:User");
    var standardUser = await userManager.FindByNameAsync(
        userCfg.GetValue("UserName", "user")!);
    if (standardUser == null)
    {
        standardUser = new AppUser
        {
            UserName = userCfg.GetValue("UserName", "user")!,
            FullName = userCfg.GetValue("FullName", "Standard User")
        };
        await userManager.CreateAsync(standardUser,
            userCfg.GetValue("Password", "user123")!);
    }
    if (!await userManager.IsInRoleAsync(standardUser, userRole))
        await userManager.AddToRoleAsync(standardUser, userRole);

    var analystCfg = app.Configuration.GetSection("SeedUsers:Analyst");
    var analystUser = await userManager.FindByNameAsync(
        analystCfg.GetValue("UserName", "analyst")!);
    if (analystUser == null)
    {
        analystUser = new AppUser
        {
            UserName = analystCfg.GetValue("UserName", "analyst")!,
            FullName = analystCfg.GetValue("FullName", "Data Analyst")
        };
        await userManager.CreateAsync(analystUser,
            analystCfg.GetValue("Password", "analyst123")!);
    }
    if (!await userManager.IsInRoleAsync(analystUser, userRole))
        await userManager.AddToRoleAsync(analystUser, userRole);

    // ── Seed configuration data ────────────────────────────────────────

    // Seed AppConfig
    var existingConfig = await db.AppConfigs.FirstOrDefaultAsync();
    if (existingConfig == null)
    {
        db.AppConfigs.Add(new AppConfig
        {
            AppName = "Excel Search Pro",
            CompanyName = "Coldwell Banker Real Estate",
            AppDescription = "Search, manage and export real estate records efficiently.",
            PlatformType = "Real Estate Data Platform",
            BrandMark = "CB",
            BrandIcon = "ESP",
            FooterText = "Data Workspace"
        });
    }

    // Seed UI strings for all pages
    var uiStrings = new List<(string key, string value, string category, string page)>
    {
        // Landing Page
        ("landing_hero_label", "REAL ESTATE DATA PLATFORM", "landing", "Index"),
        ("landing_hero_title", "Find property information with confidence.", "landing", "Index"),
        ("landing_hero_description", "A focused workspace for searching, organizing, and managing real estate Excel data.", "landing", "Index"),
        ("landing_user_title", "User", "landing", "Index"),
        ("landing_user_description", "Search across indexed data, apply filters, preview matching records, and export results.", "landing", "Index"),
        ("landing_user_feature1", "Search & filter data ✓", "landing", "Index"),
        ("landing_user_feature2", "Preview matched rows ✓", "landing", "Index"),
        ("landing_user_feature3", "Export results to Excel ✓", "landing", "Index"),
        ("landing_user_button", "Open Dashboard →", "landing", "Index"),
        ("landing_admin_title", "Admin", "landing", "Index"),
        ("landing_admin_description", "Upload files, manage indexing, monitor system health, and review activity logs.", "landing", "Index"),
        ("landing_admin_feature1", "Upload & index files ✓", "landing", "Index"),
        ("landing_admin_feature2", "Monitor indexing pipeline ✓", "landing", "Index"),
        ("landing_admin_feature3", "Manage all data ✓", "landing", "Index"),
        ("landing_admin_feature4", "Review activity logs ✓", "landing", "Index"),
        ("landing_admin_login", "Log In", "landing", "Index"),
        ("landing_admin_signup", "Sign Up", "landing", "Index"),
        ("landing_admin_options", "Admin options ⌄", "landing", "Index"),
        ("landing_hint", "Hover over the Admin card to access administrator options.", "landing", "Index"),

        // Login Page
        ("login_title", "Welcome Back", "login", "Login"),
        ("login_subtitle", "Login to Excel Search Pro", "login", "Login"),
        ("login_brand_title", "Excel Search Pro", "login", "Login"),
        ("login_brand_subtitle", "Coldwell Banker Real Estate Platform", "login", "Login"),
        ("login_description", "Search, manage and export real estate records efficiently. Upload Excel and CSV files, index data, and find information instantly through a powerful search system.", "login", "Login"),
        ("login_feature1", "Fast Data Search", "login", "Login"),
        ("login_feature2", "Excel & CSV Indexing", "login", "Login"),
        ("login_feature3", "Export Search Results", "login", "Login"),
        ("login_feature4", "Coldwell Banker Internal System", "login", "Login"),
        ("login_username_label", "Username", "login", "Login"),
        ("login_username_placeholder", "Enter username", "login", "Login"),
        ("login_password_label", "Password", "login", "Login"),
        ("login_password_placeholder", "Enter password", "login", "Login"),
        ("login_remember", "Remember Me", "login", "Login"),
        ("login_button", "Login", "login", "Login"),
        ("login_or", "OR", "login", "Login"),
        ("login_no_account", "Don't have an account?", "login", "Login"),
        ("login_create_account", "Create Account", "login", "Login"),
        ("login_back", "Back", "login", "Login"),

        // Admin Sidebar
        ("admin_sidebar_logo", "Excel Search Pro", "admin", "Sidebar"),
        ("admin_section_pipeline", "PIPELINE", "admin", "Sidebar"),
        ("admin_overview", "Overview", "admin", "Sidebar"),
        ("admin_upload", "Upload Files", "admin", "Sidebar"),
        ("admin_index_monitor", "Index Monitor", "admin", "Sidebar"),
        ("admin_section_data", "DATA", "admin", "Sidebar"),
        ("admin_all_files", "All Files", "admin", "Sidebar"),
        ("admin_failed_files", "Failed Files", "admin", "Sidebar"),
        ("admin_section_logs", "LOGS & HISTORY", "admin", "Sidebar"),
        ("admin_search_history", "Search History", "admin", "Sidebar"),
        ("admin_export_history", "Export History", "admin", "Sidebar"),
        ("admin_indexing_history", "Indexing History", "admin", "Sidebar"),
        ("admin_storage", "Storage", "admin", "Sidebar"),
        ("admin_logout", "Logout", "admin", "Sidebar"),

        // User Sidebar
        ("user_sidebar_brand", "Excel Search Pro", "user", "Sidebar"),
        ("user_back_landing", "← Back to Landing Page", "user", "Sidebar"),
        ("user_section_workspace", "WORKSPACE", "user", "Sidebar"),
        ("user_dashboard", "Dashboard", "user", "Sidebar"),
        ("user_search", "Search", "user", "Sidebar"),
        ("user_search_results", "Search Results", "user", "Sidebar"),
        ("user_my_exports", "My Exports", "user", "Sidebar"),
        ("user_footer", "Coldwell Banker Real Estate", "user", "Sidebar"),
        ("user_footer_sub", "Data Workspace", "user", "Sidebar"),
        ("user_logout", "Logout", "user", "Sidebar"),

        // Admin Overview
        ("admin_overview_title", "Admin Dashboard", "admin", "Overview"),
        ("admin_overview_subtitle", "System health and pipeline status", "admin", "Overview"),
        ("admin_overview_alert_ok", "All systems operational", "admin", "Overview"),
        ("admin_overview_alert_error", "files need attention", "admin", "Overview"),
        ("admin_overview_last_index", "last index:", "admin", "Overview"),
        ("admin_metric_files", "Files Indexed", "admin", "Overview"),
        ("admin_metric_records", "Total Records", "admin", "Overview"),
        ("admin_metric_failed", "Failed Files", "admin", "Overview"),
        ("admin_metric_storage", "Storage Used", "admin", "Overview"),
        ("admin_metric_storage_of", "of", "admin", "Overview"),
        ("admin_pipeline_title", "Admin Pipeline Status", "admin", "Overview"),
        ("admin_pipeline_upload", "Upload New Files", "admin", "Overview"),
        ("admin_view_failed", "View Failed Files", "admin", "Overview"),

        // User Dashboard
        ("user_dashboard_title", "Hello,", "user", "Index"),
        ("user_dashboard_subtitle", "Here is your real estate data workspace overview.", "user", "Index"),
        ("user_index_status", "The index is up to date.", "user", "Index"),
        ("user_index_status_files", "files ready to search.", "user", "Index"),
        ("user_metric_files", "FILES INDEXED", "user", "Index"),
        ("user_metric_files_detail", "Indexed files", "user", "Index"),
        ("user_metric_records", "TOTAL RECORDS", "user", "Index"),
        ("user_metric_records_detail", "Last indexed today", "user", "Index"),
        ("user_metric_exports", "MY EXPORTS", "user", "Index"),
        ("user_metric_exports_detail", "This month", "user", "Index"),
        ("user_metric_searches", "SEARCHES TODAY", "user", "Index"),
        ("user_metric_searches_detail", "Searches today", "user", "Index"),
        ("user_search_title", "Start a new search", "user", "Index"),
        ("user_search_subtitle", "Search across indexed real estate records and Excel files.", "user", "Index"),
        ("user_search_placeholder", "Search by building, unit, owner, price, or any column", "user", "Index"),
        ("user_search_button", "Search", "user", "Index"),
        ("user_activity_title", "Recent Activity", "user", "Index"),
        ("user_activity_subtitle", "Your latest search and export activity.", "user", "Index"),
        ("user_activity_searched", "Searched", "user", "Index"),
        ("user_activity_exported", "Exported", "user", "Index"),
        ("user_activity_rows", "rows →", "user", "Index"),
        ("user_activity_none", "No activity yet.", "user", "Index"),
        ("user_activity_start", "Start searching", "user", "Index"),
        ("user_activity_get_started", "Get started", "user", "Index"),
        ("user_landing_button", "← Landing Page", "user", "Index"),
        ("user_search_required", "Search required", "user", "Index"),
        ("user_search_required_text", "Please enter something to search.", "user", "Index")
    };

    foreach (var (key, value, category, page) in uiStrings)
    {
        var existing = await db.UIStrings.FirstOrDefaultAsync(s => s.Key == key);
        if (existing == null)
        {
            db.UIStrings.Add(new UIString
            {
                Key = key,
                Value = value,
                Category = category,
                Page = page
            });
        }
    }
    await db.SaveChangesAsync();
}

// ── Middleware ───────────────────────────────────────────────────────────

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Database = System.IO.File.Exists(dbPath) ? "Connected" : "Missing"
}));

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control",
            app.Environment.IsDevelopment() ? "no-cache" : "public,max-age=3600");
    }
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ── Graceful shutdown: WAL checkpoint + clean exit ───────────────────────

var shutdownLogger = app.Services.GetRequiredService<ILogger<Program>>();

app.Lifetime.ApplicationStopping.Register(() =>
{
    shutdownLogger.LogInformation("Shutting down — running WAL checkpoint...");

    try
    {
        using var db = app.Services.CreateScope()
            .ServiceProvider.GetRequiredService<AppDbContext>();

        // Flush WAL to main database file.
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        shutdownLogger.LogInformation("WAL checkpoint complete. Safe to exit.");
    }
    catch (Exception ex)
    {
        shutdownLogger.LogWarning(ex, "WAL checkpoint during shutdown failed (non-fatal)");
    }
});

app.Run();
