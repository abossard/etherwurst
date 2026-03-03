using SpotPriceDashboard.Data;

namespace SpotPriceDashboard.Collectors;

/// <summary>
/// Action (Grokking Simplicity): orchestrates collection on startup and on demand.
/// Runs as a BackgroundService so the dashboard is usable immediately while data loads.
/// </summary>
public sealed class CollectorService(
    IServiceProvider services,
    ILogger<CollectorService> log) : BackgroundService
{
    public static readonly string[] DefaultRegions =
    [
        "eastus", "eastus2", "westus2", "westus3",
        "northeurope", "westeurope", "uksouth", "swedencentral", "germanywestcentral",
        "southeastasia", "eastasia", "japaneast", "australiaeast",
        "centralus", "canadacentral", "brazilsouth",
        "centralindia", "koreacentral", "francecentral", "switzerlandnorth"
    ];

    // Track collection status for the UI
    public bool IsCollecting { get; private set; }
    public int RegionsCompleted { get; private set; }
    public int TotalRegions { get; private set; }
    public string? CurrentRegion { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Small delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        while (!ct.IsCancellationRequested)
        {
            await CollectAllAsync(DefaultRegions, ct);
            log.LogInformation("Next refresh in {Hours} hours.", RefreshIntervalHours);
            await Task.Delay(TimeSpan.FromHours(RefreshIntervalHours), ct);
        }
    }

    /// <summary>Refresh interval in hours. Default: every 6 hours.</summary>
    public static int RefreshIntervalHours { get; set; } = 6;

    public async Task CollectAllAsync(IReadOnlyList<string> regions, CancellationToken ct = default)
    {
        IsCollecting = true;
        RegionsCompleted = 0;
        TotalRegions = regions.Count;
        LastError = null;

        try
        {
            using var scope = services.CreateScope();
            var priceCollector = scope.ServiceProvider.GetRequiredService<RetailPriceCollector>();
            var evictionCollector = scope.ServiceProvider.GetRequiredService<EvictionRateCollector>();
            var db = scope.ServiceProvider.GetRequiredService<PriceDatabase>();

            // Collect spot prices per region
            foreach (var region in regions)
            {
                ct.ThrowIfCancellationRequested();
                CurrentRegion = region;
                try
                {
                    log.LogInformation("Collecting prices for {Region}...", region);
                    var prices = await priceCollector.CollectAsync(region, ct);
                    db.UpsertPrices(prices);
                    log.LogInformation("Stored {Count} prices for {Region}", prices.Count, region);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Failed to collect prices for {Region}", region);
                    LastError = $"Error collecting {region}: {ex.Message}";
                }
                RegionsCompleted++;
            }

            // Then try eviction rates (optional, needs Azure auth)
            CurrentRegion = "eviction rates";
            try
            {
                var rates = await evictionCollector.CollectAsync(ct);
                if (rates.Count > 0)
                    db.UpdateEvictionRates(rates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Eviction rate collection failed (non-fatal)");
            }
        }
        finally
        {
            IsCollecting = false;
            CurrentRegion = null;
            log.LogInformation("Collection complete. {Completed}/{Total} regions.",
                RegionsCompleted, TotalRegions);
        }
    }
}
