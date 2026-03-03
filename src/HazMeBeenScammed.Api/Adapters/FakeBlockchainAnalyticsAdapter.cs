using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// Fake implementation of IBlockchainAnalyticsPort that returns fabricated data.
/// This allows the application to run and be demonstrated without a real blockchain connection.
/// </summary>
public sealed class FakeBlockchainAnalyticsAdapter : IBlockchainAnalyticsPort
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<TransactionInfo>> WalletTransactionCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] KnownContracts =
    [
        "Uniswap V3 Router",
        "OpenSea: Seaport 1.5",
        "USDC Token",
        "USDT Token",
        "Wrapped Ether"
    ];

    private static readonly string[] SuspiciousNames =
    [
        null!, null!, null!, // unverified
        "DrainerBot_v2",
        "FlashLoan_Exploit",
    ];

    private static readonly string[] TokenSymbols =
        ["USDC", "USDT", "WETH", "DAI", "SHIB", "PEPE", "SCAMTOKEN", "FAKEUSDC"];

    public async IAsyncEnumerable<WalletTransaction> GetWalletActivityAsync(
        IReadOnlyList<WalletAddress> wallets,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(8, cancellationToken);

        foreach (var address in wallets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedWallet = NormalizeAddress(address.Value);
            var transactions = WalletTransactionCache.GetOrAdd(normalizedWallet, GenerateDeterministicNeighborhoodTransactions);

            foreach (var tx in transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new WalletTransaction(address, tx);
            }
        }
    }

    public async IAsyncEnumerable<TransactionDetail> GetTransactionDetailsAsync(
        IReadOnlyList<TransactionHash> hashes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken);
            var random = CreateDeterministicRandom(hash.Value);

            var tx = GenerateFakeTransaction(
                "0x" + Convert.ToHexString(GenerateRandomBytes(random, 20)).ToLowerInvariant(),
                DateTimeOffset.UtcNow.AddHours(-random.Next(1, 72)),
                random,
                hash.Value);

            var logs = GenerateFakeLogs(hash.Value);
            yield return new TransactionDetail(tx, logs);
        }
    }

    public async IAsyncEnumerable<ContractAssessment> AssessContractsAsync(
        IReadOnlyList<string> addresses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken);
            var random = CreateDeterministicRandom(address);

            if (random.Next(3) == 0) continue; // EOA — skip

            var isVerified = random.Next(2) == 0;
            var isProxy = random.Next(4) == 0;
            var bytecodeLength = random.Next(150, 360);

            yield return new ContractAssessment(
                Address: address,
                Name: isVerified ? KnownContracts[random.Next(KnownContracts.Length)] : null,
                IsVerified: isVerified,
                IsProxy: isProxy,
                ProxyImplementation: isProxy ? DeriveAddress(address, 555) : null,
                HasSuspiciouslyShortBytecode: bytecodeLength < 130,
                BytecodeLength: bytecodeLength,
                AbiFragment: isVerified ? "transfer(address,uint256)" : null);
        }
    }

    private static List<TransactionLogInfo> GenerateFakeLogs(string txHash)
    {
        var random = CreateDeterministicRandom(txHash + ":receipt");
        var logs = new List<TransactionLogInfo>();
        var topicCount = random.Next(0, 3);
        for (var i = 0; i < topicCount; i++)
        {
            var topics = new List<string>();
            if (random.Next(2) == 0)
                topics.Add("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
            else
                topics.Add("0x8c5be1e5ebec7d5bd14f714f3a5a2f8f3ecf6f6c7d8b9d5f9c4a1c6f0f8b7c3");
            topics.Add("0x" + Convert.ToHexString(GenerateRandomBytes(random, 32)).ToLowerInvariant());
            topics.Add("0x" + Convert.ToHexString(GenerateRandomBytes(random, 32)).ToLowerInvariant());

            logs.Add(new TransactionLogInfo(
                Address: DeriveAddress(txHash, i + 1),
                Topics: topics,
                Data: "0x" + Convert.ToHexString(GenerateRandomBytes(random, 32)).ToLowerInvariant()));
        }
        return logs;
    }

    private static IReadOnlyList<TransactionInfo> GenerateDeterministicNeighborhoodTransactions(string wallet)
    {
        var random = CreateDeterministicRandom(wallet);
        var now = DateTimeOffset.UtcNow;
        var transactions = new List<TransactionInfo>();

        // Generate a large realistic history (200-800 transactions)
        var totalTxCount = random.Next(200, 800);
        var peerCount = random.Next(15, 40);
        var peers = Enumerable.Range(0, peerCount).Select(i => DeriveAddress(wallet, i + 1)).ToList();

        for (var i = 0; i < totalTxCount; i++)
        {
            var peer = peers[random.Next(peers.Count)];
            var isOutgoing = random.Next(2) == 0;
            var ageDays = random.Next(1, 365);
            var ageMinutes = random.Next(0, 1440);
            var timestamp = now.AddDays(-ageDays).AddMinutes(-ageMinutes);
            var isContract = random.Next(4) == 0;
            var hasToken = random.Next(3) != 0;
            var token = hasToken ? TokenSymbols[random.Next(TokenSymbols.Length)] : "";

            transactions.Add(new TransactionInfo(
                Hash: BuildDeterministicHash($"{wallet}:{peer}:{(isOutgoing ? "out" : "in")}:{i}"),
                From: isOutgoing ? wallet : peer,
                To: isOutgoing ? (isContract ? DeriveAddress(peer, 97 + i) : peer) : wallet,
                ValueEth: decimal.Round((decimal)(random.NextDouble() * 5.0), 6),
                TokenSymbol: token,
                TokenAmount: hasToken ? decimal.Round((decimal)(random.NextDouble() * 50000), 2) : 0,
                IsContractInteraction: isContract,
                ContractName: isContract ? SuspiciousNames[random.Next(SuspiciousNames.Length)] : null,
                Timestamp: timestamp,
                Status: random.Next(15) == 0 ? "Failed" : "Success",
                InputData: isContract ? BuildInputData(random) : "0x"));
        }

        return transactions
            .OrderByDescending(t => t.Timestamp)
            .ToList();
    }

    private static TransactionInfo GenerateFakeTransaction(
        string fromAddress,
        DateTimeOffset timestamp,
        Random random,
        string? hashOverride = null)
    {
        var hasToken = random.Next(2) == 0;
        var isContract = random.Next(2) == 0;
        var contractName = isContract
            ? SuspiciousNames[random.Next(SuspiciousNames.Length)]
            : null;
        var toAddress = "0x" + Convert.ToHexString(GenerateRandomBytes(random, 20)).ToLowerInvariant();
        var hash = hashOverride ?? "0x" + Convert.ToHexString(GenerateRandomBytes(random, 32)).ToLowerInvariant();

        return new TransactionInfo(
            Hash: hash,
            From: fromAddress,
            To: toAddress,
            ValueEth: random.Next(2) == 0 ? 0 : decimal.Round((decimal)(random.NextDouble() * 2.5), 6),
            TokenSymbol: hasToken ? TokenSymbols[random.Next(TokenSymbols.Length)] : "",
            TokenAmount: hasToken ? decimal.Round((decimal)(random.NextDouble() * 10000), 2) : 0,
            IsContractInteraction: isContract,
            ContractName: contractName,
            Timestamp: timestamp,
            Status: random.Next(10) == 0 ? "Failed" : "Success",
            InputData: isContract ? BuildInputData(random) : "0x"
        );
    }

    private static string BuildInputData(Random random)
    {
        var selectors = new[]
        {
            "0x095ea7b3", // approve(address,uint256)
            "0xa9059cbb", // transfer(address,uint256)
            "0x23b872dd", // transferFrom(address,address,uint256)
            "0x7ff36ab5", // swapExactETHForTokens
            "0xb6f9de95"  // random unknown selector
        };
        var selector = selectors[random.Next(selectors.Length)];
        var payload = Convert.ToHexString(GenerateRandomBytes(random, 64)).ToLowerInvariant();
        return selector + payload;
    }

    private static Random CreateDeterministicRandom(string seedText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText.ToLowerInvariant()));
        var seed = BitConverter.ToInt32(hash, 0);
        return new Random(seed);
    }

    private static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();

    private static string DeriveAddress(string seed, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{index}"));
        return "0x" + Convert.ToHexString(hash[..20]).ToLowerInvariant();
    }

    private static string BuildDeterministicHash(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "0x" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] GenerateRandomBytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
