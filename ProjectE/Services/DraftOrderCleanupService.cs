using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ProjectE.Data;

namespace ProjectE.Services
{
    public class DraftOrderCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<DraftOrderCleanupService> _logger;

        public DraftOrderCleanupService(IServiceProvider services, ILogger<DraftOrderCleanupService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 5 minutes before starting cleanup
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Delete draft orders older than 48 hours
                    var cutoff = DateTime.UtcNow.AddHours(-48);
                    var oldDrafts = await context.Orders
                        .Where(o => o.Status == "Draft" && o.CreatedAt < cutoff)
                        .ToListAsync(stoppingToken);

                    if (oldDrafts.Any())
                    {
                        context.Orders.RemoveRange(oldDrafts);
                        await context.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation($"Cleaned up {oldDrafts.Count} old draft orders older than 48 hours");
                    }
                    else
                    {
                        _logger.LogDebug("No old draft orders found during cleanup");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during draft order cleanup");
                }

                // Run cleanup every 12 hours
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
        }
    }
}