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

/// <summary>
/// Traversal direction when building wallet graphs.
/// </summary>
public enum GraphDirection
{
    Outgoing,
    Incoming,
    Both
}

/// <summary>
/// Request settings for building a wallet flow graph.
/// </summary>
public record WalletGraphQuery(
    WalletAddress Root,
    int Depth = 2,
    GraphDirection Direction = GraphDirection.Both,
    decimal MinValueEth = 0m,
    int MaxNodes = 500,
    int MaxEdges = 1500,
    int LookbackDays = 7
);

/// <summary>
/// Node shown in the wallet graph.
/// </summary>
public record WalletGraphNode(
    string Address,
    string Label,
    bool IsSeed,
    bool IsContract,
    int InboundCount,
    int OutboundCount,
    decimal TotalInEth,
    decimal TotalOutEth
);

/// <summary>
/// Directed edge between two wallets aggregated from one or more transactions.
/// </summary>
public record WalletGraphEdge(
    string Id,
    string From,
    string To,
    decimal TotalValueEth,
    int TransactionCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string DominantToken
);

/// <summary>
/// Result payload for wallet graph traversal.
/// </summary>
public record WalletGraphResult(
    string Root,
    int Depth,
    GraphDirection Direction,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<WalletGraphNode> Nodes,
    IReadOnlyList<WalletGraphEdge> Edges
);
