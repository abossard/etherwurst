using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using SpotPriceDashboard.Data;

namespace SpotPriceDashboard.Collectors;

/// <summary>
/// Action (Grokking Simplicity): queries Azure Resource Graph for eviction rates.
/// Requires DefaultAzureCredential — fails gracefully if unavailable.
/// </summary>
public sealed class EvictionRateCollector(HttpClient http, ILogger<EvictionRateCollector> log)
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, TypeInfoResolver = AzureApiJsonContext.Default };

    public async Task<Dictionary<(string vmSize, string region), string>> CollectAsync(CancellationToken ct = default)
    {
        var results = new Dictionary<(string, string), string>();

        try
        {
            var credential = new DefaultAzureCredential();
            var tokenResult = await credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]), ct);

            var query = """
                SpotResources
                | where type =~ 'microsoft.compute/skuspotevictionrate/location'
                | project skuName = tostring(sku.name), location, evictionRate = tostring(properties.evictionRate)
            """;

            var requestBody = JsonSerializer.Serialize(new { query });
            var request = new HttpRequestMessage(HttpMethod.Post,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01")
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                log.LogWarning("Resource Graph returned {Status}: {Body}", response.StatusCode, body[..Math.Min(body.Length, 500)]);
                return results;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            var graphResponse = await JsonSerializer.DeserializeAsync<ResourceGraphResponse>(stream, JsonOpts, ct);

            if (graphResponse?.Data.Rows is { Count: > 0 } rows)
            {
                foreach (var row in rows)
                {
                    if (row.Count < 3) continue;
                    var skuName = row[0]?.ToString() ?? "";
                    var location = row[1]?.ToString() ?? "";
                    var rate = row[2]?.ToString() ?? "";

                    // Normalize SKU name: "Standard_D2s_v3" → "D2s v3"
                    var normalized = skuName.Replace("Standard_", "").Replace("_", " ");
                    if (!string.IsNullOrEmpty(normalized) && !string.IsNullOrEmpty(location))
                        results[(normalized, location)] = rate;
                }
                log.LogInformation("Collected {Count} eviction rates from Resource Graph", results.Count);
            }
        }
        catch (CredentialUnavailableException)
        {
            log.LogInformation("No Azure credentials available — skipping eviction rate collection. " +
                               "Run 'az login' for eviction data.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to collect eviction rates (non-fatal)");
        }

        return results;
    }
}
