using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Port for blockchain data access. All methods are batch-first: they accept
/// lists of inputs and stream results. Use the extension methods in
/// <see cref="BlockchainAnalyticsExtensions"/> for single-item convenience.
/// </summary>
public interface IBlockchainAnalyticsPort
{
    /// <summary>
    /// Returns the transaction history for one or more wallets.
    /// Each result is tagged with the wallet it was requested for.
    /// </summary>
    IAsyncEnumerable<WalletTransaction> GetWalletActivityAsync(
        IReadOnlyList<WalletAddress> wallets, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns transaction details (tx + receipt logs) for one or more hashes.
    /// Missing transactions are silently skipped.
    /// </summary>
    IAsyncEnumerable<TransactionDetail> GetTransactionDetailsAsync(
        IReadOnlyList<TransactionHash> hashes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assesses one or more contract addresses for risk signals.
    /// EOAs (non-contracts) are silently skipped.
    /// </summary>
    IAsyncEnumerable<ContractAssessment> AssessContractsAsync(
        IReadOnlyList<string> addresses, CancellationToken cancellationToken = default);
}
