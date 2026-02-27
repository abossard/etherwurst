using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Port (interface) for fetching raw blockchain data.
/// Implement this adapter for real providers (Etherscan, Blockscout, etc.)
/// or use the FakeBlockchainAnalyticsAdapter for development/demo.
/// </summary>
public interface IBlockchainAnalyticsPort
{
    /// <summary>
    /// Returns transactions associated with the given wallet address.
    /// </summary>
    IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
        WalletAddress address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single transaction matching the given hash.
    /// </summary>
    Task<TransactionInfo?> GetTransactionAsync(
        TransactionHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns basic information about a smart contract, or null if not a contract.
    /// </summary>
    Task<ContractInfo?> GetContractInfoAsync(
        string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a smart contract.
/// </summary>
public record ContractInfo(
    string Address,
    string? Name,
    bool IsVerified,
    bool IsProxy,
    string? AbiFragment
);
