using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HazMeBeenScammed.Tests.Integration;

/// <summary>
/// Integration tests for the /api/analyze endpoint.
/// These tests use WebApplicationFactory to test the full pipeline
/// including the FakeBlockchainAnalyticsAdapter.
/// </summary>
public class AnalyzeEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> GetAltchaToken()
    {
        // Get a challenge
        var challengeResp = await _client.GetAsync("/api/altcha/challenge");
        challengeResp.EnsureSuccessStatusCode();
        var challengeJson = await challengeResp.Content.ReadAsStringAsync();
        var challenge = JsonSerializer.Deserialize<JsonElement>(challengeJson);

        // Solve the proof-of-work (brute force the number)
        var algorithm = challenge.GetProperty("algorithm").GetString()!;
        var challengeStr = challenge.GetProperty("challenge").GetString()!;
        var salt = challenge.GetProperty("salt").GetString()!;
        var maxNumber = challenge.GetProperty("maxnumber").GetInt64();

        for (long n = 0; n <= maxNumber; n++)
        {
            var hash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(salt + n)));
            if (hash == challengeStr)
            {
                var payload = new { algorithm, challenge = challengeStr, number = n, salt, signature = challenge.GetProperty("signature").GetString() };
                return Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            }
        }
        throw new Exception("Could not solve ALTCHA challenge");
    }

    [Fact]
    public async Task GetAnalyze_WithValidWalletAddress_Returns200WithSseStream()
    {
        var token = await GetAltchaToken();
        var response = await _client.GetAsync(
            $"/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e&altcha={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetAnalyze_WithValidTransactionHash_Returns200()
    {
        var token = await GetAltchaToken();
        var response = await _client.GetAsync(
            $"/api/analyze?input=0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b&altcha={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalyze_WithValidInput_StreamContainsDataEvents()
    {
        var token = await GetAltchaToken();
        var response = await _client.GetAsync(
            $"/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e&altcha={Uri.EscapeDataString(token)}",
            HttpCompletionOption.ResponseHeadersRead);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", content);
        Assert.Contains("analysisId", content);
    }

    [Fact]
    public async Task GetAnalyze_WithValidInput_StreamContainsCompletedStage()
    {
        var token = await GetAltchaToken();
        var response = await _client.GetAsync(
            $"/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e&altcha={Uri.EscapeDataString(token)}",
            HttpCompletionOption.ResponseHeadersRead);

        var content = await response.Content.ReadAsStringAsync();
        // Enums are serialized as camelCase strings
        Assert.Contains("completed", content);
        Assert.Contains("riskScore", content);
    }

    [Fact]
    public async Task GetAnalyze_WithInvalidInput_StreamContainsFailedStage()
    {
        var token = await GetAltchaToken();
        var response = await _client.GetAsync(
            $"/api/analyze?input=not-a-valid-address&altcha={Uri.EscapeDataString(token)}");

        var content = await response.Content.ReadAsStringAsync();
        // Enums are serialized as camelCase strings
        Assert.Contains("failed", content);
    }

    [Fact]
    public async Task GetAnalyze_WithoutAltcha_Returns403()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGraph_WithValidWallet_ReturnsGraphPayload()
    {
        var response = await _client.GetAsync(
            "/api/graph?wallet=0x742d35Cc6634C0532925a3b844Bc454e4438f44e&depth=2&direction=both&maxNodes=200&maxEdges=400");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("nodeCount", content);
        Assert.Contains("edgeCount", content);
        Assert.Contains("nodes", content);
        Assert.Contains("edges", content);
    }
}
