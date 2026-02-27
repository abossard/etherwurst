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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
