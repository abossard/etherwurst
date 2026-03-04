namespace SpotPriceDashboard.Data;

// ── Data (immutable records) ── following Grokking Simplicity ──

public sealed record SpotVmPrice
{
    public required string VmSize { get; init; }
    public required string Region { get; init; }
    public decimal SpotPricePerHour { get; init; }
    public decimal OnDemandPricePerHour { get; init; }
    public int VCpus { get; init; }
    public decimal MemoryGB { get; init; }
    public required string VmFamily { get; init; }
    public required string FriendlyCategory { get; init; }
    public string? EvictionRate { get; init; }
    public DateTime LastUpdated { get; init; }
}

public sealed record RegionInfo(string Name, string DisplayName);

// ── Calculations (pure functions) ── following Grokking Simplicity ──

public static class PriceCalculations
{
    public static decimal SavingsPercent(decimal onDemand, decimal spot) =>
        onDemand > 0 ? Math.Round((1m - spot / onDemand) * 100m, 1) : 0m;

    public static decimal MonthlyCost(decimal hourlyRate) =>
        Math.Round(hourlyRate * 730m, 2);

    public static string EvictionRisk(string? rate) => rate switch
    {
        "0-5" => "Very Low",
        "5-10" => "Low",
        "10-15" => "Medium",
        "15-20" => "High",
        "20+" => "Very High",
        _ => "Unknown"
    };

    public static string EvictionColor(string? rate) => rate switch
    {
        "0-5" => "#4caf50",
        "5-10" => "#8bc34a",
        "10-15" => "#ff9800",
        "15-20" => "#f44336",
        "20+" => "#b71c1c",
        _ => "#9e9e9e"
    };

    public static (string Family, int VCpus, decimal MemoryGB) ParseVmSpecs(string skuName, string? productName)
    {
        var family = ResolveFamilyFromProduct(productName);
        var vCpus = ExtractVCpuCount(skuName);
        var memoryGB = EstimateMemory(family, vCpus);
        return (family, vCpus, memoryGB);
    }

    public static string FriendlyCategory(string family) => family switch
    {
        "B" => "Burstable",
        "D" or "Ds" => "General Purpose",
        "E" or "Es" => "Memory Optimized",
        "F" or "Fs" => "Compute Optimized",
        "L" or "Ls" => "Storage Optimized",
        "M" or "Ms" => "Memory Intensive",
        "NC" => "GPU — Compute",
        "ND" => "GPU — AI/ML",
        "NV" => "GPU — Visualization",
        "DC" or "DCs" => "Confidential",
        "H" => "High Performance",
        "A" => "Basic",
        _ => "Other"
    };

    static string ResolveFamilyFromProduct(string? productName)
    {
        if (string.IsNullOrEmpty(productName)) return "Unknown";

        // productName looks like "Virtual Machines DSv3 Series" or "Virtual Machines Eav4 Series"
        var parts = productName.Replace("Virtual Machines ", "").Replace(" Series", "").Trim();
        // Extract leading letters: DSv3 → DS, Eav4 → E, FSv2 → FS, NCv3 → NC
        var family = "";
        foreach (var c in parts)
        {
            if (char.IsLetter(c) && char.IsUpper(c)) family += c;
            else if (char.IsLetter(c) && char.IsLower(c) && family.Length > 0 && family.Length <= 2)
            {
                if (c == 'v' || c == 'a' || c == 'p' || c == 'i') break;
                family += char.ToUpper(c);
            }
            else break;
        }
        return string.IsNullOrEmpty(family) ? "Unknown" : family;
    }

    static int ExtractVCpuCount(string skuName)
    {
        // skuName: "D2s v3", "E4 v4", "NC6s v3", "B2ms"
        var digits = "";
        var foundLetter = false;
        foreach (var c in skuName)
        {
            if (char.IsLetter(c)) foundLetter = true;
            else if (char.IsDigit(c) && foundLetter)
            {
                digits += c;
                continue;
            }
            if (digits.Length > 0) break;
        }
        return int.TryParse(digits, out var n) ? n : 0;
    }

    static decimal EstimateMemory(string family, int vCpus)
    {
        if (vCpus <= 0) return 0;
        decimal ratio = family switch
        {
            "B" => 2.5m,
            "D" or "DS" or "Ds" => 4m,
            "E" or "ES" or "Es" => 8m,
            "F" or "FS" or "Fs" => 2m,
            "L" or "LS" or "Ls" => 8m,
            "M" or "MS" or "Ms" => 16m,
            "NC" or "ND" or "NV" => 12m,
            "DC" or "DCS" => 4m,
            "H" => 8m,
            "A" => 2m,
            _ => 4m
        };
        return vCpus * ratio;
    }
}

// ── Azure API response DTOs ──

public sealed class RetailPriceResponse
{
    public List<RetailPriceItem> Items { get; set; } = [];
    public string? NextPageLink { get; set; }
}

public sealed class RetailPriceItem
{
    public string CurrencyCode { get; set; } = "";
    public decimal RetailPrice { get; set; }
    public decimal UnitPrice { get; set; }
    public string ArmRegionName { get; set; } = "";
    public string MeterName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SkuName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Type { get; set; } = "";
    public string UnitOfMeasure { get; set; } = "";
    public string EffectiveStartDate { get; set; } = "";
}

public sealed class ResourceGraphResponse
{
    public ResourceGraphData Data { get; set; } = new();
}

public sealed class ResourceGraphData
{
    public List<List<object>> Rows { get; set; } = [];
    public List<ResourceGraphColumn> Columns { get; set; } = [];
}

public sealed record CollectionStatus(
    bool IsCollecting,
    string? CurrentRegion,
    int RegionsCompleted,
    int TotalRegions,
    string? LastError,
    int TotalPrices,
    DateTime? LastUpdate,
    int PricesChangedLastHour);

// ── Trading / history feature types ──

public sealed record PriceHistoryPoint(DateTime RecordedAt, decimal SpotPrice);

public sealed record BigMover(
    string VmSize,
    string Region,
    string FriendlyCategory,
    int VCpus,
    decimal MemoryGB,
    decimal CurrentPrice,
    decimal PreviousPrice,
    decimal ChangePercent);

public sealed record HeatmapCell(
    string Family,
    string Region,
    decimal SavingsPct,
    decimal AvgSpot,
    int Count);

public sealed record CalculatorResult(
    string VmSize,
    string Region,
    string FriendlyCategory,
    int VCpus,
    decimal MemoryGB,
    decimal SpotPricePerHour,
    decimal OnDemandPricePerHour,
    decimal SavingsPct,
    string? EvictionRate,
    decimal Score,
    decimal MonthlySpot,
    decimal MonthlyOnDemand,
    decimal MonthlySavings);

public sealed class ResourceGraphColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed record CollectResponse(string Message);

public sealed record ChartDataPoint(
    string Category, string Region,
    decimal AvgSpotPerVCpu, decimal AvgOnDemandPerVCpu,
    int Count);
