using System.Data;
using System.Runtime.CompilerServices;
using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// IBlockchainAnalyticsPort backed by Azure Data Explorer (Kusto) for fast
/// analytical queries on pre-indexed Ethereum data. Falls through to Erigon
/// for operations that require live node access (contract assessment, logs).
/// Uses DefaultAzureCredential (works with Workload Identity in AKS + az cli locally).
/// </summary>
public sealed class AdxBlockchainAdapter : IBlockchainAnalyticsPort, IDisposable
{
    private readonly ICslQueryProvider _queryProvider;
    private readonly string _database;
    private readonly IBlockchainAnalyticsPort? _fallback;
    private readonly ILogger<AdxBlockchainAdapter> _logger;

    public AdxBlockchainAdapter(
        string clusterUri,
        string database,
        IBlockchainAnalyticsPort? fallback,
        ILogger<AdxBlockchainAdapter> logger)
    {
        _database = database;
        _fallback = fallback;
        _logger = logger;

        var credential = new DefaultAzureCredential();
        var kcsb = new KustoConnectionStringBuilder(clusterUri, database)
            .WithAadTokenProviderAuthentication(async () =>
            {
                var token = await credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext([$"{clusterUri}/.default"]));
                return token.Token;
            });
        _queryProvider = KustoClientFactory.CreateCslQueryProvider(kcsb);
    }

    public async IAsyncEnumerable<WalletTransaction> GetWalletActivityAsync(
        IReadOnlyList<WalletAddress> wallets,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (wallets.Count == 0) yield break;

        _logger.LogInformation("ADX: batch fetching transactions for {Count} wallet(s)", wallets.Count);

        var walletLookup = wallets.ToDictionary(
            w => w.Value.ToLowerInvariant(), w => w, StringComparer.OrdinalIgnoreCase);
        var inList = string.Join(",", walletLookup.Keys.Select(w => $"'{w}'"));

        var kql = $"""
            let wallets = dynamic([{inList}]);
            Transactions
            | where from_address in~ (wallets) or to_address in~ (wallets)
            | join kind=inner (Blocks | project block_number, timestamp) on block_number
            | project transaction_hash, from_address, to_address, value_f64, success, timestamp, input, gas_used
            | order by timestamp desc
            | take {wallets.Count * 666}
            """;

        using var reader = await _queryProvider.ExecuteQueryAsync(_database, kql, new ClientRequestProperties(), cancellationToken);

        while (reader.Read())
        {
            var tx = MapTransaction(reader);
            var fromLower = tx.From.ToLowerInvariant();
            var toLower = tx.To.ToLowerInvariant();

            if (walletLookup.TryGetValue(fromLower, out var fromWallet))
                yield return new WalletTransaction(fromWallet, tx);
            if (walletLookup.TryGetValue(toLower, out var toWallet)
                && !toLower.Equals(fromLower, StringComparison.OrdinalIgnoreCase))
                yield return new WalletTransaction(toWallet, tx);
        }

        _logger.LogInformation("ADX: finished batch wallet query for {Count} wallet(s)", wallets.Count);
    }

    public async IAsyncEnumerable<TransactionDetail> GetTransactionDetailsAsync(
        IReadOnlyList<TransactionHash> hashes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (hashes.Count == 0) yield break;

        _logger.LogInformation("ADX: batch fetching {Count} transaction detail(s)", hashes.Count);

        var hashSet = new HashSet<string>(
            hashes.Select(h => h.Value.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var inList = string.Join(",", hashSet.Select(h => $"'{h}'"));

        // Fetch transaction + logs in one query via join
        var kql = $"""
            let hashes = dynamic([{inList}]);
            let txs = Transactions
                | where transaction_hash in~ (hashes)
                | join kind=inner (Blocks | project block_number, timestamp) on block_number
                | project transaction_hash, from_address, to_address, value_f64, success, timestamp, input, gas_used;
            let logs = Logs
                | where transaction_hash in~ (hashes)
                | project transaction_hash, address, topic0, topic1, topic2, topic3, data;
            txs
            | project-rename tx_hash = transaction_hash
            | join kind=leftouter (logs | project-rename tx_hash = transaction_hash) on tx_hash
            | project tx_hash, from_address, to_address, value_f64, success, timestamp, input, gas_used,
                      log_address = address, topic0, topic1, topic2, topic3, log_data = data
            """;

        using var reader = await _queryProvider.ExecuteQueryAsync(_database, kql, new ClientRequestProperties(), cancellationToken);

        var txLogs = new Dictionary<string, (TransactionInfo Tx, List<TransactionLogInfo> Logs)>(StringComparer.OrdinalIgnoreCase);
        var foundHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            var txHash = GetStringOrEmpty(reader, "tx_hash");
            foundHashes.Add(txHash);

            if (!txLogs.TryGetValue(txHash, out var entry))
            {
                var tx = MapTransactionByName(reader);
                entry = (tx, new List<TransactionLogInfo>());
                txLogs[txHash] = entry;
            }

            var logAddress = GetStringOrEmpty(reader, "log_address");
            if (!string.IsNullOrEmpty(logAddress))
            {
                var topics = new List<string>();
                foreach (var col in new[] { "topic0", "topic1", "topic2", "topic3" })
                {
                    var t = GetStringOrEmpty(reader, col);
                    if (!string.IsNullOrEmpty(t)) topics.Add(t);
                }
                entry.Logs.Add(new TransactionLogInfo(logAddress, topics, GetStringOrEmpty(reader, "log_data")));
            }
        }

        foreach (var (_, (tx, logs)) in txLogs)
            yield return new TransactionDetail(tx, logs);

        // Fall through to live node for hashes not in ADX
        if (_fallback != null)
        {
            var missing = hashes.Where(h => !foundHashes.Contains(h.Value)).ToList();
            if (missing.Count > 0)
            {
                _logger.LogInformation("ADX: {Count} tx(es) not found, falling back", missing.Count);
                await foreach (var detail in _fallback.GetTransactionDetailsAsync(missing, cancellationToken))
                    yield return detail;
            }
        }
    }

    public async IAsyncEnumerable<ContractAssessment> AssessContractsAsync(
        IReadOnlyList<string> addresses,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Contract assessment needs live node — always delegate
        if (_fallback != null)
        {
            await foreach (var assessment in _fallback.AssessContractsAsync(addresses, cancellationToken))
                yield return assessment;
        }
    }

    // ─── Mapping helpers ─────────────────────────────────────────────

    private static TransactionInfo MapTransaction(IDataReader reader)
    {
        var hash = reader.GetString(0);           // transaction_hash
        var from = reader.GetString(1);           // from_address
        var to = reader.IsDBNull(2) ? "" : reader.GetString(2); // to_address
        var valueEth = reader.IsDBNull(3) ? 0m : (decimal)reader.GetDouble(3); // value_f64
        var success = reader.GetBoolean(4);       // success
        var timestamp = reader.IsDBNull(5) ? 0L : reader.GetInt64(5); // timestamp (unix)
        var inputData = reader.IsDBNull(6) ? null : reader.GetString(6); // input
        var gasUsed = reader.IsDBNull(7) ? 0L : reader.GetInt64(7); // gas_used

        return new TransactionInfo(
            Hash: hash, From: from, To: to,
            ValueEth: Math.Round(valueEth, 6),
            TokenSymbol: "", TokenAmount: 0m,
            IsContractInteraction: !string.IsNullOrEmpty(inputData) && inputData != "0x",
            ContractName: null,
            Timestamp: DateTimeOffset.FromUnixTimeSeconds(timestamp),
            Status: success ? "Success" : "Failed",
            InputData: inputData);
    }

    private static TransactionInfo MapTransactionByName(IDataReader reader)
    {
        var hash = GetStringOrEmpty(reader, "tx_hash");
        var from = GetStringOrEmpty(reader, "from_address");
        var to = GetStringOrEmpty(reader, "to_address");
        var valueEth = GetDoubleOrZero(reader, "value_f64");
        var success = GetBoolOrFalse(reader, "success");
        var timestamp = GetLongOrZero(reader, "timestamp");
        var inputData = GetStringOrEmpty(reader, "input");

        return new TransactionInfo(
            Hash: hash, From: from, To: to,
            ValueEth: Math.Round((decimal)valueEth, 6),
            TokenSymbol: "", TokenAmount: 0m,
            IsContractInteraction: !string.IsNullOrEmpty(inputData) && inputData != "0x",
            ContractName: null,
            Timestamp: DateTimeOffset.FromUnixTimeSeconds(timestamp),
            Status: success ? "Success" : "Failed",
            InputData: inputData);
    }

    private static string GetStringOrEmpty(IDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    private static double GetDoubleOrZero(IDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0.0 : r.GetDouble(i);
    }

    private static bool GetBoolOrFalse(IDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return !r.IsDBNull(i) && r.GetBoolean(i);
    }

    private static long GetLongOrZero(IDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0L : r.GetInt64(i);
    }

    public void Dispose() => (_queryProvider as IDisposable)?.Dispose();
}
