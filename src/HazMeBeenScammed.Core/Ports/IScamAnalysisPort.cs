using HazMeBeenScammed.Core.Domain;

namespace HazMeBeenScammed.Core.Ports;

/// <summary>
/// Port (interface) for running scam analysis and streaming progress events.
/// </summary>
public interface IScamAnalysisPort
{
    /// <summary>
    /// Runs a scam analysis for the given request, emitting progress events.
    /// The final event will have Stage == Completed and contain the full Result.
    /// </summary>
    IAsyncEnumerable<AnalysisProgressEvent> AnalyzeAsync(
        AnalysisRequest request, CancellationToken cancellationToken = default);
}
