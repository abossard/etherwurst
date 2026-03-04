using MudBlazor.Services;
using SpotPriceDashboard.Api;
using SpotPriceDashboard.Collectors;
using SpotPriceDashboard.Components;
using SpotPriceDashboard.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Data ──
var dbPath = builder.Configuration.GetValue<string>("DbPath") ?? "spotprices.db";
builder.Services.AddSingleton(new PriceDatabase(dbPath));

// ── Collectors (Actions) ──
builder.Services.AddHttpClient<RetailPriceCollector>();
builder.Services.AddHttpClient<EvictionRateCollector>();
builder.Services.AddSingleton<CollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorService>());

// ── Blazor + MudBlazor ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddApexCharts();

// ── JSON serialization for trimmed build ──
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(AppJsonContext.Default));

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

// ── API endpoints ──
app.MapPricingApi();

// ── Blazor ──
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
