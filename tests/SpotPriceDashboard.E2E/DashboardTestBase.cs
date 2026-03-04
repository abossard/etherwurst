using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;
using System.IO;

namespace SpotPriceDashboard.E2E;

/// <summary>
/// Base class for SpotPriceDashboard E2E tests.
/// Set BASE_URL env var (default: http://localhost:5080) before running.
/// </summary>
[Parallelizable(ParallelScope.Self)]
[TestFixture]
public abstract class DashboardTestBase : PageTest
{
    protected string BaseUrl { get; private set; } = "http://localhost:5080";

    // Screenshots directory next to the test assembly
    protected string ScreenshotsDir { get; private set; } = Path.Combine(
        TestContext.CurrentContext.TestDirectory, "screenshots");

    [SetUp]
    public void Setup()
    {
        BaseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? BaseUrl;
        Directory.CreateDirectory(ScreenshotsDir);
    }

    protected string ScreenshotPath(string name)
        => Path.Combine(ScreenshotsDir, $"{name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");

    protected async Task Screenshot(string name)
        => await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = ScreenshotPath(name),
            FullPage = true
        });

    public override BrowserNewContextOptions ContextOptions() => new()
    {
        ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        ColorScheme = ColorScheme.Dark
    };
}
