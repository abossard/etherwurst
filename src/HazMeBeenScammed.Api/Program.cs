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
var blockscoutUrl = builder.Configuration["Blockscout:ApiUrl"];
var adxClusterUri = builder.Configuration["Adx:ClusterUri"];
var adxDatabase = builder.Configuration["Adx:Database"] ?? "ethereum";

// Register HTTP clients for configured backends
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
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(180);
        options.Retry.MaxRetryAttempts = 1;
    });
}

if (!string.IsNullOrEmpty(blockscoutUrl))
{
    builder.Services.AddHttpClient("blockscout", client =>
    {
        client.BaseAddress = new Uri(blockscoutUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}

// Build the registry with all configured adapters
builder.Services.AddSingleton<IAdapterRegistry>(sp =>
{
    // Default backend: explicit env var, or first available
    var configuredDefault = builder.Configuration["DefaultBackend"];
    var defaultBackend = !string.IsNullOrEmpty(configuredDefault) ? configuredDefault
        : !string.IsNullOrEmpty(erigonUrl) ? "erigon"
        : !string.IsNullOrEmpty(clickhouseConn) ? "clickhouse"
        : !string.IsNullOrEmpty(adxClusterUri) ? "adx"
        : !string.IsNullOrEmpty(blockscoutUrl) ? "blockscout"
        : "fake";

    var registry = new AdapterRegistry(defaultBackend);

    // Erigon adapter (if RPC URL configured)
    ErigonBlockchainAdapter? erigonAdapter = null;
    if (!string.IsNullOrEmpty(erigonUrl))
    {
        erigonAdapter = new ErigonBlockchainAdapter(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<ErigonBlockchainAdapter>>());
        registry.Register("erigon", erigonAdapter);
    }

    // Blockscout adapter (only if URL configured, with Erigon fallback)
    if (!string.IsNullOrEmpty(blockscoutUrl))
    {
        var blockscoutClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("blockscout");
        registry.Register("blockscout", new BlockscoutBlockchainAdapter(
            blockscoutClient, erigonAdapter,
            sp.GetRequiredService<ILogger<BlockscoutBlockchainAdapter>>()));
    }

    // ClickHouse adapter (with Erigon fallback for live-node ops)
    if (!string.IsNullOrEmpty(clickhouseConn))
    {
        registry.Register("clickhouse", new ClickHouseBlockchainAdapter(
            clickhouseConn, erigonAdapter,
            sp.GetRequiredService<ILogger<ClickHouseBlockchainAdapter>>()));
    }

    // ADX adapter (with Erigon fallback for live-node ops)
    if (!string.IsNullOrEmpty(adxClusterUri))
    {
        registry.Register("adx", new AdxBlockchainAdapter(
            adxClusterUri, adxDatabase, erigonAdapter,
            sp.GetRequiredService<ILogger<AdxBlockchainAdapter>>()));
    }

    // Fake adapter only when no real backends are configured (integration tests)
    if (registry.AvailableBackends.Count == 0)
        registry.Register("fake", new FakeBlockchainAnalyticsAdapter());

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

// GET /api/examples — sample addresses & tx hashes for the UI (configurable via Examples section)
app.MapGet("/api/examples", (IConfiguration config) =>
{
    var examples = config.GetSection("Examples").Get<List<ExampleEntry>>()
        ?? DefaultExamples.All;
    return Results.Ok(examples);
})
.WithName("ListExamples")
.WithTags("Config")
.WithSummary("Sample wallet addresses and transaction hashes for quick testing");

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

// ─── Example entries served to the UI ────────────────────────────
record ExampleEntry(string Label, string Value, string? Icon = null, string? Backend = null);

static class DefaultExamples
{
    public static readonly List<ExampleEntry> All =
    [
        // Original samples (work with any backend / Erigon)
        new("Sample Wallet", "0x60d0da6875CA73C6656a38cf76ffB8b3A9AEB57C", "🦊"),
        new("Sample Tx (first ERC-20)", "0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b", "📋"),

        // ClickHouse — recent blocks (~24.5 M)
        new("Whale (5.5k ETH)", "0xa9ac43f5b5e38155a288d1a01d2cbc4478e14573", "🐋", "clickhouse"),
        new("High-volume sender", "0x9696f59e4d72e237be84ffd425dcad154bf96976", "🔁", "clickhouse"),
        new("5546 ETH transfer", "0x9f7f9a6eb59265ee9fe1ef3a8864c6e0aa0a771bbe8eac85230a09cc36c7cfb5", "💸", "clickhouse"),

        // ADX — early blockchain (blocks ~3.7 M, 2017)
        new("Ethermine pool (2017)", "0x52bc44d5378309ee2abf1539bf71de1b7d7be3b5", "⛏️", "adx"),
        new("Poloniex (2017)", "0x32be343b94f860124dc4fee278fdcbd38c102d88", "🏦", "adx"),
        new("327k ETH tx", "0x60362978cf905d475780015593dfc30c3b7ec3cb4a8b692deecddddc7223bf16", "🔥", "adx"),
    ];
}
