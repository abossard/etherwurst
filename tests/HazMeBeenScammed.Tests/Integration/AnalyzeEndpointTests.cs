using System.Net;
using System.Net.Http;
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

    [Fact]
    public async Task GetAnalyze_WithValidWalletAddress_Returns200WithSseStream()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetAnalyze_WithValidTransactionHash_Returns200()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalyze_WithValidInput_StreamContainsDataEvents()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
            HttpCompletionOption.ResponseHeadersRead);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", content);
        Assert.Contains("analysisId", content);
    }

    [Fact]
    public async Task GetAnalyze_WithValidInput_StreamContainsCompletedStage()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
            HttpCompletionOption.ResponseHeadersRead);

        var content = await response.Content.ReadAsStringAsync();
        // Enums are serialized as camelCase strings
        Assert.Contains("completed", content);
        Assert.Contains("riskScore", content);
    }

    [Fact]
    public async Task GetAnalyze_WithInvalidInput_StreamContainsFailedStage()
    {
        var response = await _client.GetAsync(
            "/api/analyze?input=not-a-valid-address");

        var content = await response.Content.ReadAsStringAsync();
        // Enums are serialized as camelCase strings
        Assert.Contains("failed", content);
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
