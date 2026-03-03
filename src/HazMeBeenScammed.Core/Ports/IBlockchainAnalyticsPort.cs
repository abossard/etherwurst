using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Port for blockchain data access, oriented around analysis use cases
/// rather than low-level EVM primitives.
/// </summary>
public interface IBlockchainAnalyticsPort
{
    /// <summary>
    /// Returns the transaction history for a wallet address.
    /// </summary>
    IAsyncEnumerable<TransactionInfo> GetWalletActivityAsync(
        WalletAddress address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single transaction with its receipt logs.
    /// </summary>
    Task<TransactionDetail?> GetTransactionDetailAsync(
        TransactionHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assesses a contract address for risk signals: proxy pattern,
    /// verification status, suspiciously short bytecode.
    /// Returns null for EOAs (non-contracts).
    /// </summary>
    Task<ContractAssessment?> AssessContractAsync(
        string address, CancellationToken cancellationToken = default);
}
