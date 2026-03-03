using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;
using Microsoft.Extensions.Logging;

namespace HazMeBeenScammed.Core.Services;

/// <summary>
/// Core domain service that orchestrates scam analysis using the blockchain analytics port.
/// Follows the hexagonal architecture: pure domain logic, no framework dependencies.
/// </summary>
public sealed class ScamAnalyzer(IBlockchainAnalyticsPort analytics, ILogger<ScamAnalyzer> logger) : IScamAnalysisPort
{
    private const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    private const string ApprovalTopic = "0x8c5be1e5ebec7d5bd14f714f3a5a2f8f3ecf6f6c7d8b9d5f9c4a1c6f0f8b7c3";

    public async IAsyncEnumerable<AnalysisProgressEvent> AnalyzeAsync(
        AnalysisRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        yield return new AnalysisProgressEvent(id, AnalysisStage.Started,
            $"Starting analysis for {request.Input}", 5);

        if (request.InputType == AnalysisInputType.Unknown)
        {
            yield return new AnalysisProgressEvent(id, AnalysisStage.Failed,
                "Invalid input: must be a valid Ethereum wallet address (0x...42 chars) or transaction hash (0x...66 chars).", 0);
            yield break;
        }

        yield return new AnalysisProgressEvent(id, AnalysisStage.FetchingTransactions,
            "Fetching transactions from blockchain...", 15);

        var transactions = new List<TransactionInfo>();
        var transactionLogs = new Dictionary<string, IReadOnlyList<TransactionLogInfo>>(StringComparer.OrdinalIgnoreCase);
        string? errorMessage = null;

        try
        {
            if (request.InputType == AnalysisInputType.WalletAddress)
            {
                var wallet = new WalletAddress(request.Input);
                await foreach (var tx in analytics.GetWalletActivityAsync(wallet, cancellationToken))
                    transactions.Add(tx);
            }
            else
            {
                var hash = new TransactionHash(request.Input);
                var detail = await analytics.GetTransactionDetailAsync(hash, cancellationToken);
                if (detail is not null)
                {
                    transactions.Add(detail.Transaction);
                    transactionLogs[detail.Transaction.Hash] = detail.Logs;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        if (errorMessage is not null)
        {
            yield return new AnalysisProgressEvent(id, AnalysisStage.Failed,
                $"Failed to fetch transactions: {errorMessage}", 0);
            yield break;
        }

        yield return new AnalysisProgressEvent(id, AnalysisStage.AnalyzingContracts,
            $"Fetched {transactions.Count} transaction(s). Analyzing contracts...", 40);

        var indicators = new List<ScamIndicator>();

        foreach (var tx in transactions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (tx.IsContractInteraction && tx.ContractName is null)
                indicators.Add(new ScamIndicator(ScamIndicatorType.UnverifiedContract,
                    $"Interaction with unverified contract at {tx.To}", ScamSeverity.Warning));

            if (tx.ValueEth == 0 && !tx.IsContractInteraction)
                indicators.Add(new ScamIndicator(ScamIndicatorType.ZeroValueTransfer,
                    $"Zero-value ETH transfer in tx {tx.Hash[..10]}...", ScamSeverity.Info));
        }

        yield return new AnalysisProgressEvent(id, AnalysisStage.DetectingPatterns,
            "Detecting scam patterns...", 65);

        indicators.AddRange(DetectPatterns(transactions));
        indicators.AddRange(DetectSimplePortfolioHeuristics(request, transactions));

        var verifiableIndicators = await DetectVerifiableOnChainSignalsAsync(request, transactions, transactionLogs, cancellationToken);
        indicators.AddRange(verifiableIndicators);

        yield return new AnalysisProgressEvent(id, AnalysisStage.ComputingScore,
            "Computing risk score...", 85);

        var (verdict, score, summary) = ComputeVerdict(indicators, transactions);

        var result = new ScamAnalysisResult(
            AnalysisId: id,
            Input: request.Input,
            InputType: request.InputType,
            Verdict: verdict,
            RiskScore: score,
            Summary: summary,
            Transactions: transactions.AsReadOnly(),
            Indicators: indicators.AsReadOnly(),
            AnalyzedAt: DateTimeOffset.UtcNow
        );

        yield return new AnalysisProgressEvent(id, AnalysisStage.Completed,
            "Analysis complete.", 100, result);

        // ─── Phase 2: Deep Analysis — batch-query all counterparty wallets ─────
        if (request.InputType == AnalysisInputType.WalletAddress && transactions.Count > 0)
        {
            var inputAddr = request.Input.ToLowerInvariant();
            var counterparties = transactions
                .SelectMany(tx => new[] { tx.From, tx.To })
                .Where(a => !string.IsNullOrEmpty(a) && !a.Equals(inputAddr, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();

            if (counterparties.Count > 0)
            {
                yield return new AnalysisProgressEvent(id, AnalysisStage.DeepAnalysis,
                    $"Deep analysis: scanning {counterparties.Count} counterparty address(es)...", 100);

                var counterpartyScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Batch-query all counterparty wallets in one call
                var wallets = counterparties.Select(a => new WalletAddress(a)).ToList();
                var grouped = new Dictionary<string, List<TransactionInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var addr in counterparties)
                    grouped[addr] = new List<TransactionInfo>();

                await foreach (var wt in analytics.GetWalletActivityAsync(wallets, cancellationToken))
                {
                    var key = wt.Wallet.Value;
                    if (grouped.TryGetValue(key, out var list))
                        list.Add(wt.Transaction);
                }

                // Score each counterparty from the grouped results
                var analyzed = 0;
                foreach (var addr in counterparties)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    analyzed++;

                    var cpTxs = grouped[addr];
                    var cpIndicators = new List<ScamIndicator>();
                    foreach (var tx in cpTxs)
                    {
                        if (tx.IsContractInteraction && tx.ContractName is null)
                            cpIndicators.Add(new ScamIndicator(ScamIndicatorType.UnverifiedContract,
                                $"Unverified contract at {tx.To}", ScamSeverity.Warning));
                        if (tx.ValueEth == 0 && !tx.IsContractInteraction)
                            cpIndicators.Add(new ScamIndicator(ScamIndicatorType.ZeroValueTransfer,
                                $"Zero-value transfer", ScamSeverity.Info));
                    }
                    cpIndicators.AddRange(DetectPatterns(cpTxs));

                    var (cpVerdict, cpScore, _) = ComputeVerdict(cpIndicators, cpTxs);
                    counterpartyScores[addr] = cpScore;

                    var combinedScore = ComputeCombinedScore(score, counterpartyScores);

                    yield return new AnalysisProgressEvent(id, AnalysisStage.DeepAnalysis,
                        $"Analyzed {analyzed}/{counterparties.Count}: {addr[..8]}… (risk: {cpScore})",
                        100, null,
                        new CounterpartyRiskEvent(id, addr, cpScore, cpVerdict, cpTxs.Count, combinedScore));
                }

                var finalCombined = ComputeCombinedScore(score, counterpartyScores);
                yield return new AnalysisProgressEvent(id, AnalysisStage.DeepAnalysisComplete,
                    $"Deep analysis complete. Combined risk score: {finalCombined}", 100);
            }
        }
    }

    private static IEnumerable<ScamIndicator> DetectPatterns(List<TransactionInfo> transactions)
    {
        if (transactions.Count == 0) yield break;

        // Rapid token dump: many token transfers in short time
        var tokenTxs = transactions.Where(t => t.TokenAmount > 0).ToList();
        if (tokenTxs.Count > 5)
        {
            var timeSpan = tokenTxs.Max(t => t.Timestamp) - tokenTxs.Min(t => t.Timestamp);
            if (timeSpan.TotalMinutes < 10)
                yield return new ScamIndicator(ScamIndicatorType.RapidTokenDump,
                    $"{tokenTxs.Count} token transfers in under 10 minutes — possible dump pattern",
                    ScamSeverity.High);
        }

        // Honeypot: tokens received but never transferred out
        var receivedSymbols = transactions
            .Where(t => t.TokenAmount > 0 && !string.IsNullOrEmpty(t.TokenSymbol))
            .Select(t => t.TokenSymbol)
            .Distinct();
        foreach (var symbol in receivedSymbols)
        {
            var received = transactions.Where(t => t.TokenSymbol == symbol && t.TokenAmount > 0).Sum(t => t.TokenAmount);
            if (received > 0 && transactions.Count(t => t.TokenSymbol == symbol) == 1)
                yield return new ScamIndicator(ScamIndicatorType.HoneypotToken,
                    $"Token {symbol} received but no out-transfer detected — possible honeypot",
                    ScamSeverity.Warning);
        }

        // Fake approval: approve calls to unknown contracts
        var approvalTxs = transactions.Where(t =>
            t.IsContractInteraction && t.ContractName is null && t.ValueEth == 0).ToList();
        if (approvalTxs.Count > 2)
            yield return new ScamIndicator(ScamIndicatorType.FakeApproval,
                $"{approvalTxs.Count} approval-like calls to unverified contracts",
                ScamSeverity.Critical,
                IndicatorConfidence.Medium,
                approvalTxs.Take(3).Select(t => t.Hash).ToList());
    }

    private static IEnumerable<ScamIndicator> DetectSimplePortfolioHeuristics(
        AnalysisRequest request,
        List<TransactionInfo> transactions)
    {
        if (request.InputType != AnalysisInputType.WalletAddress || transactions.Count == 0)
        {
            yield break;
        }

        var normalized = request.Input.ToLowerInvariant();

        // Concentration score: top 3 counterparties dominate activity.
        var counterparties = transactions
            .Select(t => t.From.Equals(normalized, StringComparison.OrdinalIgnoreCase) ? t.To : t.From)
            .Where(a => !string.IsNullOrWhiteSpace(a) && !a.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (counterparties.Count >= 6)
        {
            var grouped = counterparties
                .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count())
                .OrderByDescending(c => c)
                .Take(3)
                .Sum();

            var concentrationRatio = (decimal)grouped / counterparties.Count;
            if (concentrationRatio > 0.60m)
            {
                yield return new ScamIndicator(
                    ScamIndicatorType.CounterpartyConcentration,
                    $"Top counterparties concentrate {concentrationRatio:P0} of activity.",
                    ScamSeverity.Warning,
                    IndicatorConfidence.Medium);
            }
        }

        var firstSeen = transactions.Min(t => t.Timestamp);
        var highValueTx = transactions.Any(t => t.ValueEth >= 10m);
        if ((DateTimeOffset.UtcNow - firstSeen).TotalDays <= 7 && highValueTx)
        {
            yield return new ScamIndicator(
                ScamIndicatorType.WalletAgeAnomaly,
                "Very new wallet with high-value transfers.",
                ScamSeverity.Warning,
                IndicatorConfidence.Medium,
                [firstSeen.ToString("O")]);
        }

        var failedRatio = (decimal)transactions.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) / transactions.Count;
        if (failedRatio > 0.40m && transactions.Count >= 6)
        {
            yield return new ScamIndicator(
                ScamIndicatorType.FailedTransactionSpike,
                $"High failed transaction ratio detected ({failedRatio:P0}).",
                ScamSeverity.Info,
                IndicatorConfidence.Low);
        }
    }

    private async Task<List<ScamIndicator>> DetectVerifiableOnChainSignalsAsync(
        AnalysisRequest request,
        List<TransactionInfo> transactions,
        Dictionary<string, IReadOnlyList<TransactionLogInfo>> knownLogs,
        CancellationToken cancellationToken)
    {
        var indicators = new List<ScamIndicator>();
        var contractAddresses = transactions
            .Where(t => t.IsContractInteraction && !string.IsNullOrWhiteSpace(t.To))
            .Select(t => t.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        // Batch contract assessment — one call for all contracts
        if (contractAddresses.Count > 0)
        {
            try
            {
                await foreach (var assessment in analytics.AssessContractsAsync(contractAddresses, cancellationToken))
                {
                    if (assessment.IsProxy && !string.IsNullOrEmpty(assessment.ProxyImplementation))
                    {
                        indicators.Add(new ScamIndicator(
                            ScamIndicatorType.ProxyUpgradeabilityRisk,
                            $"Upgradeable proxy pattern detected at {assessment.Address}.",
                            ScamSeverity.Warning,
                            IndicatorConfidence.Verified,
                            [assessment.Address, assessment.ProxyImplementation]));
                    }

                    if (assessment.HasSuspiciouslyShortBytecode)
                    {
                        indicators.Add(new ScamIndicator(
                            ScamIndicatorType.MaliciousBytecodeSimilarity,
                            $"Contract {assessment.Address} has unusually short bytecode, common in drainer templates.",
                            ScamSeverity.Warning,
                            IndicatorConfidence.High,
                            [assessment.Address, $"bytecodeLength={assessment.BytecodeLength}"]));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Batch contract assessment failed, skipping");
            }
        }

        var inputWallet = request.InputType == AnalysisInputType.WalletAddress
            ? request.Input.ToLowerInvariant()
            : null;

        // Batch receipt/log analysis — one call for all tx hashes not already cached
        var txsToAnalyze = transactions.Take(80).ToList();
        var hashesToFetch = txsToAnalyze
            .Where(tx => !knownLogs.ContainsKey(tx.Hash))
            .Select(tx => new TransactionHash(tx.Hash))
            .ToList();

        // Fetch all missing logs in one batch call
        var fetchedLogs = new Dictionary<string, IReadOnlyList<TransactionLogInfo>>(StringComparer.OrdinalIgnoreCase);
        if (hashesToFetch.Count > 0)
        {
            try
            {
                await foreach (var detail in analytics.GetTransactionDetailsAsync(hashesToFetch, cancellationToken))
                    fetchedLogs[detail.Transaction.Hash] = detail.Logs;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Batch receipt fetch failed, skipping log analysis");
            }
        }

        // Analyze logs for each transaction
        foreach (var tx in txsToAnalyze)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<TransactionLogInfo>? logs = null;
            if (knownLogs.TryGetValue(tx.Hash, out var cached))
                logs = cached;
            else if (fetchedLogs.TryGetValue(tx.Hash, out var fetched))
                logs = fetched;

            if (logs == null) continue;

            var hasApproval = logs.Any(l => l.Topics.Count > 0 && l.Topics[0].Equals(ApprovalTopic, StringComparison.OrdinalIgnoreCase));
            var hasTransfer = logs.Any(l => l.Topics.Count > 0 && l.Topics[0].Equals(TransferTopic, StringComparison.OrdinalIgnoreCase));

            if (hasApproval && inputWallet is not null)
            {
                var laterOutflow = transactions.Any(other =>
                    other.Timestamp > tx.Timestamp &&
                    (other.Timestamp - tx.Timestamp).TotalMinutes <= 15 &&
                    other.From.Equals(inputWallet, StringComparison.OrdinalIgnoreCase) &&
                    other.ValueEth >= 0.5m);

                if (laterOutflow)
                {
                    indicators.Add(new ScamIndicator(
                        ScamIndicatorType.ApprovalDrainPattern,
                        "Approval event followed by rapid outflow, possible drainer pattern.",
                        ScamSeverity.Critical,
                        IndicatorConfidence.Verified,
                        [tx.Hash]));
                }
            }

            if (tx.IsContractInteraction && tx.Status == "Success" && tx.ValueEth == 0m && !hasApproval && !hasTransfer)
            {
                indicators.Add(new ScamIndicator(
                    ScamIndicatorType.EventLogAnomaly,
                    $"Successful contract call with no common transfer/approval events ({tx.Hash[..10]}...).",
                    ScamSeverity.Warning,
                    IndicatorConfidence.High,
                    [tx.Hash]));
            }

            if (!string.IsNullOrWhiteSpace(tx.InputData) && tx.InputData.StartsWith("0xb6f9de95", StringComparison.OrdinalIgnoreCase))
            {
                indicators.Add(new ScamIndicator(
                    ScamIndicatorType.SuspiciousContract,
                    "Call uses unknown high-risk selector pattern.",
                    ScamSeverity.Warning,
                    IndicatorConfidence.Medium,
                    [tx.Hash, tx.InputData[..10]]));
            }
        }

        return indicators;
    }

    private static (ScamVerdict verdict, int score, string summary) ComputeVerdict(
        List<ScamIndicator> indicators, List<TransactionInfo> transactions)
    {
        if (transactions.Count == 0)
            return (ScamVerdict.Clean, 0, "No transactions found for this input.");

        var score = indicators.Sum(i =>
        {
            var baseWeight = i.Severity switch
            {
                ScamSeverity.Critical => 40,
                ScamSeverity.High => 20,
                ScamSeverity.Warning => 10,
                ScamSeverity.Info => 2,
                _ => 0
            };

            var confidenceMultiplier = i.Confidence switch
            {
                IndicatorConfidence.Low => 0.6m,
                IndicatorConfidence.Medium => 1.0m,
                IndicatorConfidence.High => 1.25m,
                IndicatorConfidence.Verified => 1.5m,
                _ => 1.0m
            };

            return (int)Math.Round(baseWeight * confidenceMultiplier, MidpointRounding.AwayFromZero);
        });
        score = Math.Min(score, 100);

        var verdict = score switch
        {
            >= 70 => ScamVerdict.ConfirmedScam,
            >= 40 => ScamVerdict.LikelyScam,
            >= 15 => ScamVerdict.Suspicious,
            _ => ScamVerdict.Clean
        };

        var summary = verdict switch
        {
            ScamVerdict.ConfirmedScam =>
                $"⚠️ HIGH RISK: Multiple scam patterns detected across {transactions.Count} transaction(s). Do not interact further.",
            ScamVerdict.LikelyScam =>
                $"🚨 LIKELY SCAM: Suspicious activity detected in {transactions.Count} transaction(s). Proceed with extreme caution.",
            ScamVerdict.Suspicious =>
                $"⚡ SUSPICIOUS: Some unusual patterns found in {transactions.Count} transaction(s). Verify before proceeding.",
            _ =>
                $"✅ LOOKS CLEAN: No significant scam patterns detected in {transactions.Count} transaction(s)."
        };

        return (verdict, score, summary);
    }

    /// <summary>
    /// Combined score: own score contributes 40%, average counterparty score contributes 60%.
    /// </summary>
    private static int ComputeCombinedScore(int ownScore, Dictionary<string, int> counterpartyScores)
    {
        if (counterpartyScores.Count == 0) return ownScore;
        var avgCounterparty = (int)counterpartyScores.Values.Average();
        return Math.Min(100, (int)(ownScore * 0.4 + avgCounterparty * 0.6));
    }
}
