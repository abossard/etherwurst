using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Core.Services;

/// <summary>
/// Core domain service that orchestrates scam analysis using the blockchain analytics port.
/// Follows the hexagonal architecture: pure domain logic, no framework dependencies.
/// </summary>
public sealed class ScamAnalyzer(IBlockchainAnalyticsPort analytics) : IScamAnalysisPort
{
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
        string? errorMessage = null;

        try
        {
            if (request.InputType == AnalysisInputType.WalletAddress)
            {
                var wallet = new WalletAddress(request.Input);
                await foreach (var tx in analytics.GetTransactionsForWalletAsync(wallet, cancellationToken))
                    transactions.Add(tx);
            }
            else
            {
                var hash = new TransactionHash(request.Input);
                var tx = await analytics.GetTransactionAsync(hash, cancellationToken);
                if (tx is not null) transactions.Add(tx);
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

        // Analyze each transaction for scam patterns
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

        // Pattern detection across all transactions
        indicators.AddRange(DetectPatterns(transactions));

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

        // ─── Phase 2: Deep Analysis — analyze counterparty addresses ─────
        if (request.InputType == AnalysisInputType.WalletAddress && transactions.Count > 0)
        {
            var inputAddr = request.Input.ToLowerInvariant();
            var counterparties = transactions
                .SelectMany(tx => new[] { tx.From, tx.To })
                .Where(a => !string.IsNullOrEmpty(a) && !a.Equals(inputAddr, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (counterparties.Count > 0)
            {
                yield return new AnalysisProgressEvent(id, AnalysisStage.DeepAnalysis,
                    $"Deep analysis: scanning {counterparties.Count} counterparty address(es)...", 100);

                var counterpartyScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var analyzed = 0;

                foreach (var addr in counterparties)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    analyzed++;

                    AnalysisProgressEvent? cpEvent = null;
                    try
                    {
                        var cpTxs = new List<TransactionInfo>();
                        var wallet = new WalletAddress(addr);
                        await foreach (var tx in analytics.GetTransactionsForWalletAsync(wallet, cancellationToken))
                            cpTxs.Add(tx);

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

                        cpEvent = new AnalysisProgressEvent(id, AnalysisStage.DeepAnalysis,
                            $"Analyzed {analyzed}/{counterparties.Count}: {addr[..8]}… (risk: {cpScore})",
                            100, null,
                            new CounterpartyRiskEvent(id, addr, cpScore, cpVerdict, cpTxs.Count, combinedScore));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        counterpartyScores[addr] = 0;
                    }

                    if (cpEvent is not null)
                        yield return cpEvent;
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
                ScamSeverity.Critical);
    }

    private static (ScamVerdict verdict, int score, string summary) ComputeVerdict(
        List<ScamIndicator> indicators, List<TransactionInfo> transactions)
    {
        if (transactions.Count == 0)
            return (ScamVerdict.Clean, 0, "No transactions found for this input.");

        var score = indicators.Sum(i => i.Severity switch
        {
            ScamSeverity.Critical => 40,
            ScamSeverity.High => 20,
            ScamSeverity.Warning => 10,
            ScamSeverity.Info => 2,
            _ => 0
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
    /// A clean wallet interacting mostly with scammers → high combined score (victim).
    /// A scammy wallet interacting with clean wallets → moderate combined score.
    /// </summary>
    private static int ComputeCombinedScore(int ownScore, Dictionary<string, int> counterpartyScores)
    {
        if (counterpartyScores.Count == 0) return ownScore;
        var avgCounterparty = (int)counterpartyScores.Values.Average();
        return Math.Min(100, (int)(ownScore * 0.4 + avgCounterparty * 0.6));
    }
}
