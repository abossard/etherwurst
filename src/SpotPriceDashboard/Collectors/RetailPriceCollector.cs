using System.Text.Json;
using SpotPriceDashboard.Data;

namespace SpotPriceDashboard.Collectors;

/// <summary>
/// Action (Grokking Simplicity): calls the Azure Retail Prices API.
/// No authentication required. Handles pagination automatically.
/// Deep module (Philosophy of Software Design): callers just call CollectAsync.
/// </summary>
public sealed class RetailPriceCollector(HttpClient http, ILogger<RetailPriceCollector> log)
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = AzureApiJsonContext.Default
    };

    public async Task<List<SpotVmPrice>> CollectAsync(string region, CancellationToken ct = default)
    {
        var spotPrices = new Dictionary<string, RetailPriceItem>();
        var onDemandPrices = new Dictionary<string, RetailPriceItem>();

        // Fetch all VM consumption prices for this region
        var filter = $"serviceName eq 'Virtual Machines' and armRegionName eq '{region}' and type eq 'Consumption' and unitOfMeasure eq '1 Hour'";
        var url = $"https://prices.azure.com/api/retail/prices?$filter={Uri.EscapeDataString(filter)}&api-version=2023-01-01-preview";

        var pageCount = 0;
        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();
            var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStreamAsync(ct);
            var page = await JsonSerializer.DeserializeAsync<RetailPriceResponse>(body, JsonOpts, ct);
            if (page is null) break;

            foreach (var item in page.Items)
            {
                if (item.MeterName.Contains("Spot", StringComparison.OrdinalIgnoreCase))
                {
                    // Normalize: strip "Spot" suffix from SkuName for matching
                    var key = item.SkuName.Replace(" Spot", "").Trim();
                    spotPrices.TryAdd(key, item);
                }
                else if (!item.MeterName.Contains("Low Priority", StringComparison.OrdinalIgnoreCase))
                    onDemandPrices.TryAdd(item.SkuName, item);
            }

            url = page.NextPageLink;
            pageCount++;
            if (pageCount % 5 == 0)
                log.LogInformation("Region {Region}: fetched {Pages} pages, {Spot} spot / {OnDemand} on-demand",
                    region, pageCount, spotPrices.Count, onDemandPrices.Count);
        }

        log.LogInformation("Region {Region}: total {Spot} spot VMs, {OnDemand} on-demand VMs",
            region, spotPrices.Count, onDemandPrices.Count);

        // Merge spot + on-demand into unified records
        var results = new List<SpotVmPrice>();
        var now = DateTime.UtcNow;

        foreach (var (skuName, spotItem) in spotPrices)
        {
            var (family, vCpus, memoryGB) = PriceCalculations.ParseVmSpecs(skuName, spotItem.ProductName);
            var friendlyCat = PriceCalculations.FriendlyCategory(family);
            var onDemandPrice = onDemandPrices.TryGetValue(skuName, out var od) ? od.RetailPrice : 0m;

            if (vCpus <= 0) continue;

            results.Add(new SpotVmPrice
            {
                VmSize = skuName,
                Region = region,
                SpotPricePerHour = spotItem.RetailPrice,
                OnDemandPricePerHour = onDemandPrice,
                VCpus = vCpus,
                MemoryGB = memoryGB,
                VmFamily = family,
                FriendlyCategory = friendlyCat,
                LastUpdated = now
            });
        }

        return results;
    }
}
