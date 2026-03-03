using System.Runtime.CompilerServices;
using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Single-item convenience wrappers around the batch-first IBlockchainAnalyticsPort.
/// These exist so callers that only need one wallet/hash/address don't have to
/// construct lists and unwrap tagged results.
/// </summary>
public static class BlockchainAnalyticsExtensions
{
    public static async IAsyncEnumerable<TransactionInfo> GetWalletActivityAsync(
        this IBlockchainAnalyticsPort port,
        WalletAddress wallet,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var wt in port.GetWalletActivityAsync([wallet], cancellationToken))
            yield return wt.Transaction;
    }

    public static async Task<TransactionDetail?> GetTransactionDetailAsync(
        this IBlockchainAnalyticsPort port,
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        await foreach (var detail in port.GetTransactionDetailsAsync([hash], cancellationToken))
            return detail;
        return null;
    }

    public static async Task<ContractAssessment?> AssessContractAsync(
        this IBlockchainAnalyticsPort port,
        string address,
        CancellationToken cancellationToken = default)
    {
        await foreach (var assessment in port.AssessContractsAsync([address], cancellationToken))
            return assessment;
        return null;
    }
}
