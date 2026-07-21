# ExcelSearch Pro — Coldwell Banker Real Estate

Internal data platform for searching, indexing, and managing real estate Excel and CSV records. Built for Coldwell Banker Real Estate.

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 (cross-platform — Mac & Windows) |
| Framework | ASP.NET Core MVC |
| Database | SQLite via Entity Framework Core |
| Auth | ASP.NET Core Identity (cookie-based) |
| Excel Parsing | ExcelDataReader + ClosedXML |
| Frontend | Bootstrap 3, jQuery, SweetAlert2 |
| Web Server | Kestrel |

## Quick Start

**Option A — launch script (easiest):**
```bash
./run.sh
```

**Option B — manual:**
```bash
cd "ExcelSearch - CB"
dotnet run
```

Opens on **http://localhost:5000**. The database is created automatically on first run — no setup needed.

> The folder you're standing in (`ExcelSearch - CB/`) is the solution root.
> The project is one level deeper at `ExcelSearch - CB/ExcelSearch - CB.csproj`.
> The launch script handles this automatically.

### Default Accounts

| Username | Password | Role |
|---|---|---|
| `admin` | `admin123` | Admin (full access) |
| `analyst` | `analyst123` | User (search + export) |

Create additional accounts via the Signup page. All new accounts get the **User** role by default.

## Features

### User Dashboard
- **Search** — Partial, Exact, and Flexible (fuzzy) search across all indexed Excel/CSV columns
- **Column-specific search** — Limit searches to specific columns
- **Filter Builder** — Multi-condition filters (field + operator + value) with AND logic
- **Preview** — View source file data with read-only table (500 row cap)
- **Export** — Export search results as XLSX or CSV
- **My Exports** — Download history of all your exports
- **Dashboard** — Real-time stats: files indexed, total records, exports this month, searches today

### Admin Panel
- **Overview** — System health dashboard with pipeline status
- **Upload & Index** — Upload `.xlsx`, `.xls`, or `.csv` files; parsed and indexed into the database
- **Index Monitor** — Per-file status tracking (Uploaded → Indexing → Indexed / Failed)
- **All Files** — Browse all uploaded files with View, Re-Index, and Delete actions
- **Failed Files** — Files that failed indexing with error details and Retry/Delete
- **Search History** — All user searches with terms, modes, results
- **Export History** — All user exports with file sizes and download tracking
- **Storage** — Real disk usage, database size, indexed record count, capacity monitoring

### Authentication & Authorization
- Cookie-based login with persistent sessions (8-hour expiry)
- `[Authorize]` protection on all admin and user dashboard routes
- Role-based identity (Admin / User)
- Anti-forgery tokens on all POST forms

## Project Structure

```
ExcelSearch - CB.sln
ExcelSearch - CB/
├── Program.cs                  # App startup, DI, middleware, Identity seeding
├── appsettings.json            # Storage limits, logging config
├── ExcelSearch - CB.csproj     # SDK-style .NET 10 project
├── Data/
│   ├── AppDbContext.cs         # EF Core context
│   ├── AppUser.cs              # Identity user (extends IdentityUser)
│   ├── IndexedFile.cs          # Uploaded file metadata
│   ├── IndexedRecord.cs        # Denormalized cell-level search index
│   ├── SearchLog.cs            # Per-search audit log
│   └── ExportLog.cs            # Per-export audit log
├── Services/
│   └── FileIndexingService.cs  # Excel/CSV parsing and indexing engine
├── Models/                     # ViewModels (unchanged design)
├── Controllers/
│   ├── HomeController.cs       # Landing, Login, Signup, Logout
│   ├── AdminController.cs      # Admin panel (auth-protected)
│   └── UserDashboardController.cs  # Search, export, preview (auth-protected)
├── Views/                      # Razor views (Bootstrap 3)
│   ├── Shared/_Layout.cshtml
│   ├── Shared/_AdminSidebar.cshtml
│   ├── Shared/_UserSidebar.cshtml
│   ├── Home/                   # Landing, Login, Signup
│   ├── Admin/                  # Overview, Upload, IndexMonitor, AllFiles, etc.
│   └── UserDashboard/          # Index, Search, FilterBuilder, SearchResults, etc.
├── wwwroot/
│   ├── Content/                # CSS files
│   ├── Scripts/                # JS files (jQuery, Bootstrap)
│   ├── fonts/                  # Glyphicons
│   ├── Uploads/                # Uploaded Excel/CSV files
│   └── Exports/                # Generated export files
└── App_Data/
    └── ExcelSearch.db          # SQLite database (auto-created)
```

## Configuration

`appsettings.json`:
```json
{
  "Storage": {
    "LimitGB": 50,
    "MaxFileSizeMB": 100
  }
}
```

## How the Search Index Works

When a file is uploaded, every cell is stored as a denormalized record:

```
{ FileId, WorksheetName, RowNumber, ColumnName, ColumnValue }
```

This allows fast column-specific SQL queries without re-parsing files:

- **All Columns** → `WHERE ColumnValue LIKE '%term%'`
- **Specific Column** → `WHERE ColumnName = 'TOWER' AND ColumnValue LIKE '%term%'`
- **Filter Builder** → Intersection of `(FileId, RowNumber)` sets across conditions

## Supported File Formats

- `.xlsx` (Excel 2007+)
- `.xls` (Excel 97-2003)
- `.csv` (Comma-separated values)

## Decision Log

See [DECISION_LOG.md](DECISION_LOG.md) for all architectural decisions made during development.
