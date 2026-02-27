using System.Text.Json;
using System.Text.Json.Serialization;
using HazMeBeenScammed.Api.Adapters;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using HazMeBeenScammed.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Hexagonal architecture: register ports and adapters
// Use real Erigon adapter when endpoints are configured, otherwise fall back to fake
var erigonUrl = builder.Configuration["Erigon:RpcUrl"];
if (!string.IsNullOrEmpty(erigonUrl))
{
    builder.Services.AddHttpClient("erigon-rpc", client =>
    {
        client.BaseAddress = new Uri(erigonUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    });

    var blockscoutUrl = builder.Configuration["Blockscout:ApiUrl"] ?? "http://blockscout.blockscout.svc.cluster.local:4000";
    builder.Services.AddHttpClient("blockscout", client =>
    {
        client.BaseAddress = new Uri(blockscoutUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    });

    builder.Services.AddSingleton<IBlockchainAnalyticsPort, ErigonBlockchainAdapter>();
}
else
{
    builder.Services.AddSingleton<IBlockchainAnalyticsPort, FakeBlockchainAnalyticsAdapter>();
}
builder.Services.AddScoped<IScamAnalysisPort, ScamAnalyzer>();
builder.Services.AddScoped<IWalletGraphPort, WalletGraphService>();

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                builder.Configuration["AllowedOrigins"]?.Split(',') ?? ["http://localhost:5174", "https://localhost:7174"])
              .AllowAnyHeader()
              .AllowAnyMethod()));

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseHttpsRedirection();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

// POST /api/analyze — starts analysis and streams SSE events
app.MapGet("/api/analyze", async (
    string input,
    IScamAnalysisPort analyzer,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.Connection = "keep-alive";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var request = new AnalysisRequest(input.Trim());

    await foreach (var evt in analyzer.AnalyzeAsync(request, cancellationToken))
    {
        var json = JsonSerializer.Serialize(evt, jsonOptions);
        await http.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
})
.WithName("AnalyzeInput")
.WithTags("Analysis")
.WithSummary("Analyze a wallet address or transaction hash for scam patterns (SSE stream)")
.Produces<AnalysisProgressEvent>(200, "text/event-stream");

// GET /api/graph - builds a wallet flow graph for interactive visualization
app.MapGet("/api/graph", async (
    string wallet,
    int depth,
    string? direction,
    decimal? minValueEth,
    int? maxNodes,
    int? maxEdges,
    int? lookbackDays,
    IWalletGraphPort graphPort,
    CancellationToken cancellationToken) =>
{
    var normalizedWallet = wallet.Trim();
    if (!WalletAddress.IsValid(normalizedWallet))
    {
        return Results.BadRequest(new { error = "Invalid wallet address format." });
    }

    if (!Enum.TryParse<GraphDirection>(direction ?? "Both", ignoreCase: true, out var parsedDirection))
    {
        parsedDirection = GraphDirection.Both;
    }

    var query = new WalletGraphQuery(
        Root: new WalletAddress(normalizedWallet),
        Depth: depth,
        Direction: parsedDirection,
        MinValueEth: minValueEth ?? 0m,
        MaxNodes: maxNodes ?? 500,
        MaxEdges: maxEdges ?? 1500,
        LookbackDays: lookbackDays ?? 7);

    var result = await graphPort.BuildGraphAsync(query, cancellationToken);
    return Results.Ok(result);
})
.WithName("BuildWalletGraph")
.WithTags("Graph")
.WithSummary("Build a traversed wallet input/output graph for workbench visualization")
.Produces<WalletGraphResult>(200)
.Produces(400);

app.Run();

// Make Program accessible for integration testing
public partial class Program { }
