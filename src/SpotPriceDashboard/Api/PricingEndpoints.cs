using SpotPriceDashboard.Collectors;
using SpotPriceDashboard.Data;

namespace SpotPriceDashboard.Api;

/// <summary>
/// Deep module (Philosophy of Software Design): simple URL → rich result.
/// Maps minimal API endpoints for programmatic access to pricing data.
/// </summary>
public static class PricingEndpoints
{
    public static void MapPricingApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/prices", (
            PriceDatabase db,
            string? regions,
            string? families,
            int? minVCpus,
            int? maxVCpus,
            decimal? minMemory,
            decimal? maxMemory) =>
        {
            var regionList = regions?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var familyList = families?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            return Results.Ok(db.QueryPrices(regionList, familyList, minVCpus, maxVCpus, minMemory, maxMemory));
        });

        api.MapGet("/regions", (PriceDatabase db) => Results.Ok(db.GetRegions()));
        api.MapGet("/families", (PriceDatabase db) => Results.Ok(db.GetFamilies()));

        api.MapGet("/status", (CollectorService collector, PriceDatabase db) =>
            Results.Ok(new CollectionStatus(
                collector.IsCollecting,
                collector.CurrentRegion,
                collector.RegionsCompleted,
                collector.TotalRegions,
                collector.LastError,
                db.GetPriceCount(),
                db.GetLastUpdate())));

        api.MapPost("/collect", async (CollectorService collector, CancellationToken ct) =>
        {
            if (collector.IsCollecting)
                return Results.Conflict(new CollectResponse("Collection already in progress"));

            _ = Task.Run(() => collector.CollectAllAsync(CollectorService.DefaultRegions, ct), ct);
            return Results.Accepted("/api/status", new CollectResponse("Collection started"));
        });
    }
}
