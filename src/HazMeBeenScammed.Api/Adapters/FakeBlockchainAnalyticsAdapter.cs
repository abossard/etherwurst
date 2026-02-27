using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// Fake implementation of IBlockchainAnalyticsPort that returns fabricated data.
/// This allows the application to run and be demonstrated without a real blockchain connection.
/// Replace with a real adapter (Etherscan, Blockscout, etc.) for production use.
/// </summary>
public sealed class FakeBlockchainAnalyticsAdapter : IBlockchainAnalyticsPort
{
    private static readonly Random _rng = new(42);

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

    public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
        WalletAddress address,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Simulate network latency
        await Task.Delay(300, cancellationToken);

        var count = _rng.Next(4, 12);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-_rng.Next(1, 30));

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
            yield return GenerateFakeTransaction(address.Value, baseTime.AddMinutes(i * _rng.Next(1, 180)));
        }
    }

    public async Task<TransactionInfo?> GetTransactionAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        return GenerateFakeTransaction(
            "0x" + Convert.ToHexString(GenerateRandomBytes(20)).ToLowerInvariant(),
            DateTimeOffset.UtcNow.AddHours(-_rng.Next(1, 72)),
            hash.Value);
    }

    public async Task<ContractInfo?> GetContractInfoAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(150, cancellationToken);

        if (_rng.Next(3) == 0) return null; // 1/3 chance not a contract

        var isVerified = _rng.Next(2) == 0;
        return new ContractInfo(
            Address: address,
            Name: isVerified ? KnownContracts[_rng.Next(KnownContracts.Length)] : null,
            IsVerified: isVerified,
            IsProxy: _rng.Next(4) == 0,
            AbiFragment: isVerified ? "transfer(address,uint256)" : null
        );
    }

    private static TransactionInfo GenerateFakeTransaction(
        string fromAddress, DateTimeOffset timestamp, string? hashOverride = null)
    {
        var hasToken = _rng.Next(2) == 0;
        var isContract = _rng.Next(2) == 0;
        var contractName = isContract
            ? SuspiciousNames[_rng.Next(SuspiciousNames.Length)]
            : null;
        var toAddress = "0x" + Convert.ToHexString(GenerateRandomBytes(20)).ToLowerInvariant();
        var hash = hashOverride ?? "0x" + Convert.ToHexString(GenerateRandomBytes(32)).ToLowerInvariant();

        return new TransactionInfo(
            Hash: hash,
            From: fromAddress,
            To: toAddress,
            ValueEth: _rng.Next(2) == 0 ? 0 : (decimal)(_rng.NextDouble() * 2.5),
            TokenSymbol: hasToken ? TokenSymbols[_rng.Next(TokenSymbols.Length)] : "",
            TokenAmount: hasToken ? (decimal)(_rng.NextDouble() * 10000) : 0,
            IsContractInteraction: isContract,
            ContractName: contractName,
            Timestamp: timestamp,
            Status: _rng.Next(10) == 0 ? "Failed" : "Success"
        );
    }

    private static byte[] GenerateRandomBytes(int count)
    {
        var bytes = new byte[count];
        _rng.NextBytes(bytes);
        return bytes;
    }
}
