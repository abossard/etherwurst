using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using HazMeBeenScammed.Core.Services;

namespace HazMeBeenScammed.Tests.Unit;

public class WalletGraphServiceTests
{
    private const string RootWallet = "0x742d35cc6634c0532925a3b844bc454e4438f44e";

    [Fact]
    public async Task BuildGraphAsync_WithDepthTwo_ReturnsConnectedGraph()
    {
        var service = new WalletGraphService(new StubBlockchainAnalyticsPort());
        var query = new WalletGraphQuery(
            Root: new WalletAddress(RootWallet),
            Depth: 2,
            Direction: GraphDirection.Both,
            MinValueEth: 0m,
            MaxNodes: 200,
            MaxEdges: 400,
            LookbackDays: 30);

        var result = await service.BuildGraphAsync(query);

        Assert.NotNull(result);
        Assert.Equal(RootWallet, result.Root);
        Assert.True(result.NodeCount >= 3);
        Assert.True(result.EdgeCount >= 2);
        Assert.Contains(result.Nodes, n => n.IsSeed && n.Address == RootWallet);
    }

    [Fact]
    public async Task BuildGraphAsync_RespectsDirectionFilter()
    {
        var service = new WalletGraphService(new StubBlockchainAnalyticsPort());
        var query = new WalletGraphQuery(
            Root: new WalletAddress(RootWallet),
            Depth: 1,
            Direction: GraphDirection.Outgoing,
            MinValueEth: 0m,
            MaxNodes: 200,
            MaxEdges: 400,
            LookbackDays: 30);

        var result = await service.BuildGraphAsync(query);

        Assert.All(result.Edges, e => Assert.Equal(RootWallet, e.From));
    }

    [Fact]
    public async Task BuildGraphAsync_RespectsMinValueFilter()
    {
        var service = new WalletGraphService(new StubBlockchainAnalyticsPort());
        var query = new WalletGraphQuery(
            Root: new WalletAddress(RootWallet),
            Depth: 2,
            Direction: GraphDirection.Both,
            MinValueEth: 0.8m,
            MaxNodes: 200,
            MaxEdges: 400,
            LookbackDays: 30);

        var result = await service.BuildGraphAsync(query);

        Assert.DoesNotContain(result.Edges, e => e.TotalValueEth < 0.8m);
    }

    private sealed class StubBlockchainAnalyticsPort : IBlockchainAnalyticsPort
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
            WalletAddress address,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var wallet = address.Value.ToLowerInvariant();

            if (wallet == RootWallet)
            {
                yield return new TransactionInfo(
                    Hash: "0x" + new string('1', 64),
                    From: RootWallet,
                    To: "0x1111111111111111111111111111111111111111",
                    ValueEth: 1.2m,
                    TokenSymbol: "USDC",
                    TokenAmount: 100m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: Now.AddHours(-6),
                    Status: "Success");

                yield return new TransactionInfo(
                    Hash: "0x" + new string('2', 64),
                    From: "0x2222222222222222222222222222222222222222",
                    To: RootWallet,
                    ValueEth: 0.4m,
                    TokenSymbol: "",
                    TokenAmount: 0m,
                    IsContractInteraction: false,
                    ContractName: null,
                    Timestamp: Now.AddHours(-4),
                    Status: "Success");

                yield break;
            }

            if (wallet == "0x1111111111111111111111111111111111111111")
            {
                yield return new TransactionInfo(
                    Hash: "0x" + new string('3', 64),
                    From: "0x1111111111111111111111111111111111111111",
                    To: "0x3333333333333333333333333333333333333333",
                    ValueEth: 0.9m,
                    TokenSymbol: "DAI",
                    TokenAmount: 50m,
                    IsContractInteraction: true,
                    ContractName: "Router",
                    Timestamp: Now.AddHours(-2),
                    Status: "Success");
            }
        }

        public Task<TransactionInfo?> GetTransactionAsync(TransactionHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<TransactionInfo?>(null);

        public Task<ContractInfo?> GetContractInfoAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContractInfo?>(null);

        public Task<string?> GetBytecodeAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("0x");

        public Task<string?> GetStorageAtAsync(string address, string slot, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("0x" + new string('0', 64));

        public Task<TransactionReceiptInfo?> GetTransactionReceiptAsync(TransactionHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<TransactionReceiptInfo?>(new TransactionReceiptInfo(hash.Value, "0x1", []));
    }
}
