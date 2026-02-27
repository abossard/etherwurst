using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Port for traversing wallet transactions and building a graph result for visualization.
/// </summary>
public interface IWalletGraphPort
{
    Task<WalletGraphResult> BuildGraphAsync(
        WalletGraphQuery query,
        CancellationToken cancellationToken = default);
}
