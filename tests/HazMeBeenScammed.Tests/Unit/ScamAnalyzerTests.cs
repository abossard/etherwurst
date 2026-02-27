using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using HazMeBeenScammed.Core.Services;

namespace HazMeBeenScammed.Tests.Unit;

public class ScamAnalyzerTests
{
    private readonly ScamAnalyzer _analyzer;

    public ScamAnalyzerTests()
    {
        _analyzer = new ScamAnalyzer(new StubBlockchainAnalytics(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ScamAnalyzer>.Instance);
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

        var completedEvent = events.FirstOrDefault(e => e.Stage == AnalysisStage.Completed);
        Assert.NotNull(completedEvent);
        Assert.NotNull(completedEvent.Result);
        Assert.Equal(100, completedEvent.ProgressPercent);

        // Deep analysis follows the completed event for wallet analyses
        var lastEvent = events.LastOrDefault();
        Assert.NotNull(lastEvent);
        Assert.True(lastEvent.Stage == AnalysisStage.DeepAnalysisComplete || lastEvent.Stage == AnalysisStage.Completed);
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
        var result = events.FirstOrDefault(e => e.Stage == AnalysisStage.Completed)?.Result;

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

    [Fact]
    public async Task AnalyzeAsync_HighConcentrationAndNewWallet_EmitsPortfolioIndicators()
    {
        var analyzer = new ScamAnalyzer(new HighRiskStubBlockchainAnalytics(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ScamAnalyzer>.Instance);
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");

        var events = await analyzer.AnalyzeAsync(request).ToListAsync();
        var completed = events.First(e => e.Stage == AnalysisStage.Completed);
        Assert.NotNull(completed.Result);

        var indicators = completed.Result!.Indicators;
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.CounterpartyConcentration);
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.WalletAgeAnomaly);
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.FailedTransactionSpike);
    }

    [Fact]
    public async Task AnalyzeAsync_VerifiableSignals_EmitProxyAndApprovalDrainIndicators()
    {
        var analyzer = new ScamAnalyzer(new HighRiskStubBlockchainAnalytics(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ScamAnalyzer>.Instance);
        var request = new AnalysisRequest("0x742d35Cc6634C0532925a3b844Bc454e4438f44e");

        var events = await analyzer.AnalyzeAsync(request).ToListAsync();
        var completed = events.First(e => e.Stage == AnalysisStage.Completed);
        Assert.NotNull(completed.Result);

        var indicators = completed.Result!.Indicators;
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.ProxyUpgradeabilityRisk);
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.ApprovalDrainPattern);
        Assert.Contains(indicators, i => i.Type == ScamIndicatorType.EventLogAnomaly);
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

        public async Task<string?> GetBytecodeAsync(string address, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return "0x";
        }

        public async Task<string?> GetStorageAtAsync(string address, string slot, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return "0x" + new string('0', 64);
        }

        public async Task<TransactionReceiptInfo?> GetTransactionReceiptAsync(
            TransactionHash hash,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return new TransactionReceiptInfo(hash.Value, "0x1", []);
        }
    }

    private sealed class HighRiskStubBlockchainAnalytics : IBlockchainAnalyticsPort
    {
        private const string RootWallet = "0x742d35cc6634c0532925a3b844bc454e4438f44e";

        public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
            WalletAddress address,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var wallet = address.Value.ToLowerInvariant();

            if (wallet == RootWallet)
            {
                var now = DateTimeOffset.UtcNow;
                yield return new TransactionInfo(
                    Hash: "0x" + new string('a', 64),
                    From: RootWallet,
                    To: "0x9999999999999999999999999999999999999999",
                    ValueEth: 0m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: true,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-25),
                    Status: "Success",
                    InputData: "0x095ea7b3" + new string('0', 64));

                yield return new TransactionInfo(
                    Hash: "0x" + new string('b', 64),
                    From: RootWallet,
                    To: "0x9999999999999999999999999999999999999999",
                    ValueEth: 2.4m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-20),
                    Status: "Failed");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('c', 64),
                    From: RootWallet,
                    To: "0x9999999999999999999999999999999999999999",
                    ValueEth: 1.7m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-15),
                    Status: "Failed");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('d', 64),
                    From: RootWallet,
                    To: "0x8888888888888888888888888888888888888888",
                    ValueEth: 12m,
                    TokenSymbol: "USDC",
                    TokenAmount: 500m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-10),
                    Status: "Success");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('e', 64),
                    From: RootWallet,
                    To: "0x9999999999999999999999999999999999999999",
                    ValueEth: 0.8m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-8),
                    Status: "Failed");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('f', 64),
                    From: RootWallet,
                    To: "0x7777777777777777777777777777777777777777",
                    ValueEth: 1.1m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-5),
                    Status: "Failed");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('1', 64),
                    From: RootWallet,
                    To: "0x6666666666666666666666666666666666666666",
                    ValueEth: 0m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: true,
                    ContractName: null,
                    Timestamp: now.AddMinutes(-2),
                    Status: "Success",
                    InputData: "0xb6f9de95" + new string('0', 64));
            }
        }

        public Task<TransactionInfo?> GetTransactionAsync(TransactionHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<TransactionInfo?>(null);

        public Task<ContractInfo?> GetContractInfoAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContractInfo?>(new ContractInfo(address, null, false, true, null));

        public Task<string?> GetBytecodeAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("0x" + new string('f', 180));

        public Task<string?> GetStorageAtAsync(string address, string slot, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("0x0000000000000000000000001111111111111111111111111111111111111111");

        public Task<TransactionReceiptInfo?> GetTransactionReceiptAsync(TransactionHash hash, CancellationToken cancellationToken = default)
        {
            var logs = hash.Value.EndsWith("a", StringComparison.OrdinalIgnoreCase)
                ? new List<TransactionLogInfo>
                {
                    new(
                        Address: "0x9999999999999999999999999999999999999999",
                        Topics:
                        [
                            "0x8c5be1e5ebec7d5bd14f714f3a5a2f8f3ecf6f6c7d8b9d5f9c4a1c6f0f8b7c3",
                            "0x0",
                            "0x0"
                        ],
                        Data: "0x0")
                }
                : [];

            return Task.FromResult<TransactionReceiptInfo?>(
                new TransactionReceiptInfo(hash.Value, "0x1", logs));
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
