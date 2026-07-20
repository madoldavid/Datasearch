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
using System.IO;
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

    if (!await roleManager.RoleExistsAsync("Admin"))
        await roleManager.CreateAsync(new IdentityRole("Admin"));
    if (!await roleManager.RoleExistsAsync("User"))
        await roleManager.CreateAsync(new IdentityRole("User"));

    var adminUser = await userManager.FindByNameAsync("admin");
    if (adminUser == null)
    {
        adminUser = new AppUser { UserName = "admin", FullName = "Administrator" };
        await userManager.CreateAsync(adminUser, "admin123");
    }
    if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
        await userManager.AddToRoleAsync(adminUser, "Admin");

    var analystUser = await userManager.FindByNameAsync("analyst");
    if (analystUser == null)
    {
        analystUser = new AppUser { UserName = "analyst", FullName = "Data Analyst" };
        await userManager.CreateAsync(analystUser, "analyst123");
    }
    if (!await userManager.IsInRoleAsync(analystUser, "User"))
        await userManager.AddToRoleAsync(analystUser, "User");
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
