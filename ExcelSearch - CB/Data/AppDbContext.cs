using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch___CB.Data
{
    public class AppDbContext : IdentityDbContext<AppUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<IndexedFile> IndexedFiles { get; set; }
        public DbSet<IndexedRecord> IndexedRecords { get; set; }
        public DbSet<SearchLog> SearchLogs { get; set; }
        public DbSet<ExportLog> ExportLogs { get; set; }
        public DbSet<SearchSnapshot> SearchSnapshots { get; set; }
        public DbSet<DeletionLog> DeletionLogs { get; set; }
        public DbSet<AppConfig> AppConfigs { get; set; }
        public DbSet<UIString> UIStrings { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ── IndexedRecord: search performance ─────────────────

            builder.Entity<IndexedRecord>()
                .HasIndex(r => new { r.ColumnName, r.ColumnValue });

            builder.Entity<IndexedRecord>()
                .HasIndex(r => r.ColumnValue);

            builder.Entity<IndexedRecord>()
                .HasIndex(r => r.IndexedFileId);

            builder.Entity<IndexedRecord>()
                .HasIndex(r => new { r.IndexedFileId, r.ColumnName, r.ColumnValue });

            // ── IndexedFile ───────────────────────────────────────

            builder.Entity<IndexedFile>()
                .HasIndex(f => f.Status);

            builder.Entity<IndexedFile>()
                .HasIndex(f => f.FilePath);

            // ── Audit logs ────────────────────────────────────────

            builder.Entity<SearchLog>().HasIndex(l => l.SearchTime);
            builder.Entity<SearchLog>().HasIndex(l => l.UserId);

            builder.Entity<ExportLog>().HasIndex(l => l.ExportTime);
            builder.Entity<ExportLog>().HasIndex(l => l.UserId);
        }
    }
}
