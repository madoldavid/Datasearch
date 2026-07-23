using ExcelSearch___CB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Services
{
    public class IndexingBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<IndexingBackgroundService> _logger;

        public IndexingBackgroundService(IServiceProvider serviceProvider, ILogger<IndexingBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("IndexingBackgroundService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                        var indexingService = scope.ServiceProvider.GetRequiredService<FileIndexingService>();

                        IndexedFile fileToProcess = null;

                        using (var db = dbFactory.CreateDbContext())
                        {
                            // Find the oldest file that needs processing.
                            // If a file is stuck in "Indexing" from a previous crash, pick it up too.
                            fileToProcess = await db.IndexedFiles
                                .Where(f => f.Status == "Pending" || f.Status == "Indexing")
                                .OrderBy(f => f.UploadedAt)
                                .FirstOrDefaultAsync(stoppingToken);
                        }

                        if (fileToProcess != null)
                        {
                            _logger.LogInformation("Background service picked up file {FileId}: {FileName}", fileToProcess.Id, fileToProcess.FileName);
                            
                            // Process the file
                            indexingService.ProcessFile(fileToProcess.Id);
                        }
                        else
                        {
                            // No files to process right now, wait a bit
                            await Task.Delay(2000, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing background indexing task.");
                    await Task.Delay(5000, stoppingToken); // Backoff on generic error
                }
            }

            _logger.LogInformation("IndexingBackgroundService is stopping.");
        }
    }
}
