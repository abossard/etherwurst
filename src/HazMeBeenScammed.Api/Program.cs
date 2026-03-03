using System.Text.Json;
using System.Text.Json.Serialization;
using HazMeBeenScammed.Api.Adapters;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using HazMeBeenScammed.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ─── Adapter registry: register all available backends ───────────
var clickhouseConn = builder.Configuration["ClickHouse:ConnectionString"];
var erigonUrl = builder.Configuration["Erigon:RpcUrl"];
var blockscoutUrl = builder.Configuration["Blockscout:ApiUrl"]
                    ?? "http://blockscout.blockscout.svc.cluster.local:4000";

// Always register Erigon + Blockscout HTTP clients if configured
if (!string.IsNullOrEmpty(erigonUrl))
{
    builder.Services.AddHttpClient("erigon-rpc", client =>
    {
        client.BaseAddress = new Uri(erigonUrl);
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    }).AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
        options.Retry.MaxRetryAttempts = 1;
    });

    builder.Services.AddHttpClient("blockscout", client =>
    {
        client.BaseAddress = new Uri(blockscoutUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}
else
{
    // Register blockscout client even without Erigon (standalone mode)
    builder.Services.AddHttpClient("blockscout", client =>
    {
        client.BaseAddress = new Uri(blockscoutUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}

// Build the registry with all configured adapters
builder.Services.AddSingleton<IAdapterRegistry>(sp =>
{
    // Determine default: best available backend
    var defaultBackend = !string.IsNullOrEmpty(clickhouseConn) ? "clickhouse"
        : !string.IsNullOrEmpty(erigonUrl) ? "erigon"
        : "fake";

    var registry = new AdapterRegistry(defaultBackend);

    // Always register fake adapter (always available for testing)
    registry.Register("fake", new FakeBlockchainAnalyticsAdapter());

    // Erigon adapter (if RPC URL configured)
    ErigonBlockchainAdapter? erigonAdapter = null;
    if (!string.IsNullOrEmpty(erigonUrl))
    {
        erigonAdapter = new ErigonBlockchainAdapter(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<ErigonBlockchainAdapter>>());
        registry.Register("erigon", erigonAdapter);
    }

    // Blockscout adapter (with Erigon fallback for bytecode/storage)
    var blockscoutClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("blockscout");
    registry.Register("blockscout", new BlockscoutBlockchainAdapter(
        blockscoutClient, erigonAdapter,
        sp.GetRequiredService<ILogger<BlockscoutBlockchainAdapter>>()));

    // ClickHouse adapter (with Erigon fallback for live-node ops)
    if (!string.IsNullOrEmpty(clickhouseConn))
    {
        registry.Register("clickhouse", new ClickHouseBlockchainAdapter(
            clickhouseConn, erigonAdapter,
            sp.GetRequiredService<ILogger<ClickHouseBlockchainAdapter>>()));
    }

    var names = string.Join(", ", registry.AvailableBackends);
    sp.GetRequiredService<ILogger<AdapterRegistry>>()
        .LogInformation("Adapter registry: [{Backends}], default: {Default}", names, defaultBackend);

    return registry;
});

// IBlockchainAnalyticsPort resolves to default adapter (for services that don't support switching)
builder.Services.AddSingleton<IBlockchainAnalyticsPort>(sp =>
    sp.GetRequiredService<IAdapterRegistry>().GetAdapter(null));

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

// GET /api/backends — list available backends for the UI switcher
app.MapGet("/api/backends", (IAdapterRegistry registry) =>
    Results.Ok(new
    {
        available = registry.AvailableBackends,
        @default = registry.DefaultBackend
    }))
.WithName("ListBackends")
.WithTags("Config")
.WithSummary("List available blockchain data backends");

// POST /api/analyze — starts analysis and streams SSE events
app.MapGet("/api/analyze", async (
    string input,
    string? backend,
    IAdapterRegistry registry,
    IServiceProvider sp,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.Connection = "keep-alive";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var adapter = registry.GetAdapter(backend);
    var analyzer = new ScamAnalyzer(adapter, sp.GetRequiredService<ILogger<ScamAnalyzer>>());
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
    string? backend,
    IAdapterRegistry registry,
    IServiceProvider sp,
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

    var adapter = registry.GetAdapter(backend);
    var graphService = new WalletGraphService(adapter);
    var result = await graphService.BuildGraphAsync(query, cancellationToken);
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
