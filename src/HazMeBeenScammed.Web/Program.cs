using HazMeBeenScammed.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register typed HttpClient for the API backend
builder.Services.AddHttpClient("api", client =>
{
    var apiUrl = builder.Configuration["services:api:https:0"]
                 ?? builder.Configuration["services:api:http:0"]
                 ?? "http://localhost:5249";
    client.BaseAddress = new Uri(apiUrl);
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();

// Proxy the ALTCHA challenge endpoint so the browser widget can fetch it
app.MapGet("/api/altcha/challenge", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("api");
    var response = await client.GetAsync("/api/altcha/challenge");
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
