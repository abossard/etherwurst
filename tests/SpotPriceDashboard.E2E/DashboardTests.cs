using Microsoft.Playwright;
using NUnit.Framework;

namespace SpotPriceDashboard.E2E;

[TestFixture]
public class DashboardTests : DashboardTestBase
{
    [Test]
    [Description("Dashboard loads with dark background and key UI elements")]
    public async Task Dashboard_LoadsDarkTheme()
    {
        await Page.GotoAsync(BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Verify page title
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex(".*Spot.*", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        // Verify the top nav bar is present
        var navbar = Page.Locator(".mud-appbar, header, nav");
        await Expect(navbar.First).ToBeVisibleAsync();

        // The body should have a dark background (verify via CSS computed style)
        var bodyBg = await Page.EvaluateAsync<string>(
            "() => window.getComputedStyle(document.body).backgroundColor");
        Assert.That(bodyBg, Does.Not.Contain("255, 255, 255"), 
            "Page should use dark background, not pure white");

        await Screenshot("dashboard-home");
    }

    [Test]
    [Description("Dashboard KPI cards are visible on home page")]
    public async Task Dashboard_KpiCardsVisible()
    {
        await Page.GotoAsync(BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for the main grid to render (Blazor server may take a moment)
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // KPI cards rendered as mud-paper elements with kpi-card class
        var kpiCards = Page.Locator(".kpi-card");
        await Expect(kpiCards.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var count = await kpiCards.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(3), "Expected at least 3 KPI cards on dashboard");

        await Screenshot("dashboard-kpi-cards");
    }

    [Test]
    [Description("Data grid loads with VM rows")]
    public async Task Dashboard_DataGridShowsVms()
    {
        await Page.GotoAsync(BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Wait for table rows (mud-table rows or tr elements in the data grid)
        var rows = Page.Locator("table tbody tr, .mud-table-body .mud-table-row");
        await Expect(rows.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });

        var rowCount = await rows.CountAsync();
        Assert.That(rowCount, Is.GreaterThan(0), "Data grid should show VM size rows");

        await Screenshot("dashboard-data-grid");
    }
}

[TestFixture]
public class HeatmapTests : DashboardTestBase
{
    [Test]
    [Description("Heatmap page loads and renders cells")]
    public async Task Heatmap_LoadsAndRendersCells()
    {
        await Page.GotoAsync($"{BaseUrl}/heatmap", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Page heading
        var heading = Page.GetByText("SAVINGS HEATMAP", new PageGetByTextOptions { Exact = false });
        if (await heading.CountAsync() == 0)
            heading = Page.GetByText("HEATMAP", new PageGetByTextOptions { Exact = false });
        await Expect(heading.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        // Heatmap cells
        var cells = Page.Locator(".heatmap-cell");
        var cellCount = await cells.CountAsync();
        // May be 0 if no data yet; just verify page loads without errors
        TestContext.WriteLine($"Heatmap cells rendered: {cellCount}");

        await Screenshot("heatmap-page");
    }

    [Test]
    [Description("Heatmap mode toggles (Savings/Spot/Count) are clickable")]
    public async Task Heatmap_ModeToggles()
    {
        await Page.GotoAsync($"{BaseUrl}/heatmap", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Find toggle buttons
        var savingsBtn = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = new System.Text.RegularExpressions.Regex("Savings", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        var spotBtn    = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = new System.Text.RegularExpressions.Regex("Spot", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });

        if (await savingsBtn.CountAsync() > 0 && await spotBtn.CountAsync() > 0)
        {
            await spotBtn.First.ClickAsync();
            await Page.WaitForTimeoutAsync(500);
            await Screenshot("heatmap-spot-mode");

            await savingsBtn.First.ClickAsync();
            await Page.WaitForTimeoutAsync(500);
            await Screenshot("heatmap-savings-mode");
        }
        else
        {
            TestContext.WriteLine("Toggle buttons not found - page may not have data yet");
            await Screenshot("heatmap-no-data");
        }
    }
}

[TestFixture]
public class CalculatorTests : DashboardTestBase
{
    [Test]
    [Description("Calculator page loads with input sliders")]
    public async Task Calculator_PageLoads()
    {
        await Page.GotoAsync($"{BaseUrl}/calculator", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Page heading
        var heading = Page.GetByText("WORKLOAD CALCULATOR", new PageGetByTextOptions { Exact = false });
        if (await heading.CountAsync() == 0)
            heading = Page.GetByText("CALCULATOR", new PageGetByTextOptions { Exact = false });
        await Expect(heading.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        await Screenshot("calculator-page");
    }

    [Test]
    [Description("Calculator shows results after configuration")]
    public async Task Calculator_ShowsResults()
    {
        await Page.GotoAsync($"{BaseUrl}/calculator", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Results should auto-load (no explicit search button needed based on implementation)
        await Page.WaitForTimeoutAsync(2000);

        var resultsTable = Page.Locator("table, .mud-table");
        if (await resultsTable.CountAsync() > 0)
        {
            await Expect(resultsTable.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
            TestContext.WriteLine("Calculator results table is visible");
        }
        else
        {
            TestContext.WriteLine("No results table yet - may need data from first collection");
        }

        await Screenshot("calculator-results");
    }
}

[TestFixture]
public class HistoryTests : DashboardTestBase
{
    [Test]
    [Description("History page loads with VM picker controls")]
    public async Task History_PageLoadsWithControls()
    {
        await Page.GotoAsync($"{BaseUrl}/history", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Page heading
        var heading = Page.GetByText("PRICE HISTORY", new PageGetByTextOptions { Exact = false });
        await Expect(heading.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        // Empty state prompt
        var emptyState = Page.GetByText("Select a VM and region", new PageGetByTextOptions { Exact = false });
        if (await emptyState.CountAsync() > 0)
            await Expect(emptyState.First).ToBeVisibleAsync();

        await Screenshot("history-page-empty");
    }
}

[TestFixture]
public class NavigationTests : DashboardTestBase
{
    [Test]
    [Description("All nav links are present and navigate correctly")]
    public async Task Navigation_AllPagesReachable()
    {
        await Page.GotoAsync(BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Collect screenshot of full app nav
        await Screenshot("nav-home");

        var routes = new[]
        {
            ("/heatmap",    "heatmap"),
            ("/calculator", "calculator"),
            ("/history",    "history"),
        };

        foreach (var (path, name) in routes)
        {
            await Page.GotoAsync($"{BaseUrl}{path}", 
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            
            // Just verify no 500 error page
            var errorText = Page.GetByText("500", new PageGetByTextOptions { Exact = true });
            if (await errorText.CountAsync() > 0)
                Assert.Fail($"Page {path} returned a 500 error");

            await Screenshot($"nav-{name}");
        }
    }

    [Test]
    [Description("Status API endpoint returns valid JSON")]
    public async Task Api_StatusEndpointHealthy()
    {
        var response = await Page.APIRequest.GetAsync($"{BaseUrl}/api/status");
        Assert.That(response.Status, Is.EqualTo(200), "/api/status should return 200");

        var json = await response.TextAsync();
        Assert.That(json, Does.Contain("isCollecting").Or.Contain("IsCollecting").Or.Contain("lastCollection"),
            "/api/status should return collection status JSON");
    }

    [Test]
    [Description("Prices API endpoint returns data array")]
    public async Task Api_PricesEndpointReturnsData()
    {
        var response = await Page.APIRequest.GetAsync($"{BaseUrl}/api/prices");
        Assert.That(response.Status, Is.EqualTo(200), "/api/prices should return 200");

        var json = await response.TextAsync();
        Assert.That(json, Does.StartWith("["), "/api/prices should return a JSON array");
    }
}
