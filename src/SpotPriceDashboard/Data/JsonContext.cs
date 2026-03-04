using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotPriceDashboard.Data;

// Context for Azure API responses (PascalCase properties)
[JsonSerializable(typeof(RetailPriceResponse))]
[JsonSerializable(typeof(ResourceGraphResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class AzureApiJsonContext : JsonSerializerContext;

// Context for our API output (camelCase)
[JsonSerializable(typeof(CollectionStatus))]
[JsonSerializable(typeof(CollectResponse))]
[JsonSerializable(typeof(List<SpotVmPrice>))]
[JsonSerializable(typeof(List<RegionInfo>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<PriceHistoryPoint>))]
[JsonSerializable(typeof(List<BigMover>))]
[JsonSerializable(typeof(List<HeatmapCell>))]
[JsonSerializable(typeof(List<CalculatorResult>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class AppJsonContext : JsonSerializerContext;
