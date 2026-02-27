namespace HazMeBeenScammed.Core.Domain;

/// <summary>
/// Represents an Ethereum wallet address.
/// </summary>
public record WalletAddress(string Value)
{
    public static bool IsValid(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        value.Length == 42;

    public override string ToString() => Value;
}

/// <summary>
/// Represents an Ethereum transaction hash.
/// </summary>
public record TransactionHash(string Value)
{
    public static bool IsValid(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        value.Length == 66;

    public override string ToString() => Value;
}

/// <summary>
/// The subject of an analysis — either a wallet address or a transaction hash.
/// </summary>
public record AnalysisRequest(string Input)
{
    public AnalysisInputType InputType =>
        WalletAddress.IsValid(Input) ? AnalysisInputType.WalletAddress :
        TransactionHash.IsValid(Input) ? AnalysisInputType.TransactionHash :
        AnalysisInputType.Unknown;
}

public enum AnalysisInputType { Unknown, WalletAddress, TransactionHash }

/// <summary>
/// Information about a single transaction retrieved from the blockchain.
/// </summary>
public record TransactionInfo(
    string Hash,
    string From,
    string To,
    decimal ValueEth,
    string TokenSymbol,
    decimal TokenAmount,
    bool IsContractInteraction,
    string? ContractName,
    DateTimeOffset Timestamp,
    string Status
);

/// <summary>
/// A single red-flag indicator detected during scam analysis.
/// </summary>
public record ScamIndicator(
    ScamIndicatorType Type,
    string Description,
    ScamSeverity Severity
);

public enum ScamIndicatorType
{
    SuspiciousContract,
    DrainerPattern,
    FlashLoanAttack,
    HoneypotToken,
    FakeApproval,
    RapidTokenDump,
    ZeroValueTransfer,
    PhishingContract,
    UnverifiedContract,
    HighGasFee,
    SandwichAttack,
    CleanTransaction
}

public enum ScamSeverity { Info, Warning, High, Critical }

public enum ScamVerdict { Clean, Suspicious, LikelyScam, ConfirmedScam }

/// <summary>
/// Aggregated result of a scam analysis.
/// </summary>
public record ScamAnalysisResult(
    string AnalysisId,
    string Input,
    AnalysisInputType InputType,
    ScamVerdict Verdict,
    int RiskScore,
    string Summary,
    IReadOnlyList<TransactionInfo> Transactions,
    IReadOnlyList<ScamIndicator> Indicators,
    DateTimeOffset AnalyzedAt
);

/// <summary>
/// A progress event emitted during live streaming analysis.
/// </summary>
public record AnalysisProgressEvent(
    string AnalysisId,
    AnalysisStage Stage,
    string Message,
    int ProgressPercent,
    ScamAnalysisResult? Result = null
);

public enum AnalysisStage
{
    Started,
    FetchingTransactions,
    AnalyzingContracts,
    DetectingPatterns,
    ComputingScore,
    Completed,
    Failed
}
