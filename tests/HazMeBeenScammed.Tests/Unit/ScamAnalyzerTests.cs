using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using HazMeBeenScammed.Core.Services;

namespace HazMeBeenScammed.Tests.Unit;

public class ScamAnalyzerTests
{
    private readonly ScamAnalyzer _analyzer;

    public ScamAnalyzerTests()
    {
        _analyzer = new ScamAnalyzer(new StubBlockchainAnalytics());
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownInput_ReturnsFailed()
    {
        var request = new AnalysisRequest("not-valid");
        var events = await _analyzer.AnalyzeAsync(request).ToListAsync();

        Assert.Contains(events, e => e.Stage == AnalysisStage.Failed);
    }

    [Fact]
    public async Task AnalyzeAsync_ValidWallet_ReturnsCompletedWithResult()
    {
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");
        var events = await _analyzer.AnalyzeAsync(request).ToListAsync();

        var completedEvent = events.LastOrDefault();
        Assert.NotNull(completedEvent);
        Assert.Equal(AnalysisStage.Completed, completedEvent.Stage);
        Assert.NotNull(completedEvent.Result);
        Assert.Equal(100, completedEvent.ProgressPercent);
    }

    [Fact]
    public async Task AnalyzeAsync_ValidTransaction_ReturnsCompletedWithResult()
    {
        var request = new AnalysisRequest("0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204b93aaa9b6d64c1b0726b9e4b");
        var events = await _analyzer.AnalyzeAsync(request).ToListAsync();

        var completedEvent = events.LastOrDefault();
        Assert.NotNull(completedEvent);
        Assert.Equal(AnalysisStage.Completed, completedEvent.Stage);
        Assert.NotNull(completedEvent.Result);
    }

    [Fact]
    public async Task AnalyzeAsync_ProgressEvents_AreInIncreasingOrder()
    {
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");
        var events = await _analyzer.AnalyzeAsync(request).ToListAsync();

        // Progress should never go backwards (last event has 100)
        var nonFailed = events.Where(e => e.Stage != AnalysisStage.Failed).ToList();
        for (var i = 1; i < nonFailed.Count; i++)
            Assert.True(nonFailed[i].ProgressPercent >= nonFailed[i - 1].ProgressPercent,
                $"Progress went backwards at index {i}: {nonFailed[i - 1].ProgressPercent} -> {nonFailed[i].ProgressPercent}");
    }

    [Fact]
    public async Task AnalyzeAsync_Result_HasRiskScore_Between0And100()
    {
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");
        var events = await _analyzer.AnalyzeAsync(request).ToListAsync();
        var result = events.Last().Result;

        Assert.NotNull(result);
        Assert.InRange(result.RiskScore, 0, 100);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancellation_StopsStream()
    {
        var cts = new CancellationTokenSource();
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");

        var events = new List<AnalysisProgressEvent>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in _analyzer.AnalyzeAsync(request, cts.Token))
            {
                events.Add(evt);
                if (events.Count >= 2) cts.Cancel();
            }
        });
    }

    // Stub analytics that always returns clean transactions
    private sealed class StubBlockchainAnalytics : IBlockchainAnalyticsPort
    {
        public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
            WalletAddress address,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new TransactionInfo(
                Hash: "0xabc123" + new string('0', 60),
                From: address.Value,
                To: "0x1234567890123456789012345678901234567890",
                ValueEth: 0.1m,
                TokenSymbol: "ETH",
                TokenAmount: 0,
                IsContractInteraction: false,
                ContractName: null,
                Timestamp: DateTimeOffset.UtcNow.AddHours(-1),
                Status: "Success"
            );
        }

        public async Task<TransactionInfo?> GetTransactionAsync(
            TransactionHash hash,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return new TransactionInfo(
                Hash: hash.Value,
                From: "0x1111111111111111111111111111111111111111",
                To: "0x2222222222222222222222222222222222222222",
                ValueEth: 1.5m,
                TokenSymbol: "",
                TokenAmount: 0,
                IsContractInteraction: false,
                ContractName: null,
                Timestamp: DateTimeOffset.UtcNow.AddHours(-2),
                Status: "Success"
            );
        }

        public async Task<ContractInfo?> GetContractInfoAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return null;
        }
    }
}

// Extension method for convenience
file static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }
}
