using System.Data;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// IBlockchainAnalyticsPort backed by ClickHouse for fast analytical queries
/// on pre-indexed Ethereum data. Falls through to Erigon RPC for operations
/// that require live node access (contract assessment, transaction detail).
/// </summary>
public sealed class ClickHouseBlockchainAdapter : IBlockchainAnalyticsPort
{
    private readonly string _connectionString;
    private readonly IBlockchainAnalyticsPort? _fallback;
    private readonly ILogger<ClickHouseBlockchainAdapter> _logger;

    public ClickHouseBlockchainAdapter(
        string connectionString,
        IBlockchainAnalyticsPort? fallback,
        ILogger<ClickHouseBlockchainAdapter> logger)
    {
        _connectionString = connectionString;
        _fallback = fallback;
        _logger = logger;
    }

    public async IAsyncEnumerable<WalletTransaction> GetWalletActivityAsync(
        IReadOnlyList<WalletAddress> wallets,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (wallets.Count == 0) yield break;

        _logger.LogInformation("ClickHouse: batch fetching transactions for {Count} wallet(s)", wallets.Count);

        // Build a set for fast lookup and SQL IN clause
        var walletSet = new HashSet<string>(wallets.Select(w => w.Value.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var walletLookup = wallets.ToDictionary(w => w.Value.ToLowerInvariant(), w => w, StringComparer.OrdinalIgnoreCase);
        var inClause = string.Join(",", walletSet.Select(w => $"'{w}'"));

        var sql = $"""
            SELECT
                t.transaction_hash, t.from_address, t.to_address, t.value_f64,
                t.success, b.timestamp, t.input, t.gas_used
            FROM default.transactions t
            JOIN default.blocks b ON t.block_number = b.block_number
            WHERE t.from_address IN ({inClause}) OR t.to_address IN ({inClause})
            ORDER BY t.block_number DESC
            LIMIT {wallets.Count * 666}
            """;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var tx = MapFromReader(reader);
            var fromLower = tx.From.ToLowerInvariant();
            var toLower = tx.To.ToLowerInvariant();

            // Tag with each queried wallet this tx belongs to
            if (walletLookup.TryGetValue(fromLower, out var fromWallet))
                yield return new WalletTransaction(fromWallet, tx);
            if (walletLookup.TryGetValue(toLower, out var toWallet) && !toLower.Equals(fromLower, StringComparison.OrdinalIgnoreCase))
                yield return new WalletTransaction(toWallet, tx);
        }

        _logger.LogInformation("ClickHouse: finished batch wallet query for {Count} wallet(s)", wallets.Count);
    }

    public async IAsyncEnumerable<TransactionDetail> GetTransactionDetailsAsync(
        IReadOnlyList<TransactionHash> hashes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (hashes.Count == 0) yield break;

        _logger.LogInformation("ClickHouse: batch fetching {Count} transaction detail(s)", hashes.Count);

        var inClause = string.Join(",", hashes.Select(h => $"'{h.Value.ToLowerInvariant()}'"));

        var sql = $"""
            SELECT
                t.transaction_hash, t.from_address, t.to_address, t.value_f64,
                t.success, b.timestamp, t.input, t.gas_used
            FROM default.transactions t
            JOIN default.blocks b ON t.block_number = b.block_number
            WHERE t.transaction_hash IN ({inClause})
            """;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var foundHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tx = MapFromReader(reader);
            foundHashes.Add(tx.Hash);
            // ClickHouse doesn't store receipt logs — delegate to fallback for logs
            IReadOnlyList<TransactionLogInfo> logs = [];
            if (_fallback != null)
            {
                var detail = await _fallback.GetTransactionDetailAsync(new TransactionHash(tx.Hash), cancellationToken);
                if (detail != null) logs = detail.Logs;
            }
            yield return new TransactionDetail(tx, logs);
        }

        // Fall through to live node for hashes not in ClickHouse
        if (_fallback != null)
        {
            var missing = hashes.Where(h => !foundHashes.Contains(h.Value)).ToList();
            if (missing.Count > 0)
            {
                _logger.LogInformation("ClickHouse: {Count} tx(es) not found, falling back", missing.Count);
                await foreach (var detail in _fallback.GetTransactionDetailsAsync(missing, cancellationToken))
                    yield return detail;
            }
        }
    }

    public async IAsyncEnumerable<ContractAssessment> AssessContractsAsync(
        IReadOnlyList<string> addresses,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Contract assessment needs live node + metadata — always delegate
        if (_fallback != null)
        {
            await foreach (var assessment in _fallback.AssessContractsAsync(addresses, cancellationToken))
                yield return assessment;
        }
    }

    // ─── Mapping ─────────────────────────────────────────────────────

    private static TransactionInfo MapFromReader(IDataReader reader)
    {
        var hash = reader.GetString(0);
        var from = reader.GetString(1);
        var to = reader.IsDBNull(2) ? "" : reader.GetString(2);
        var valueRaw = reader.GetValue(3);
        var statusRaw = reader.GetValue(4);
        var timestampRaw = reader.GetValue(5);
        var inputData = reader.IsDBNull(6) ? null : reader.GetString(6);
        var gasUsed = reader.IsDBNull(7) ? 0UL : Convert.ToUInt64(reader.GetValue(7));

        // value_f64 is Wei as Float64 — convert to ETH
        var valueEth = valueRaw switch
        {
            double d => (decimal)(d / 1e18),
            float f => (decimal)(f / 1e18),
            decimal dec => dec / 1_000_000_000_000_000_000m,
            _ => ConvertToEth(valueRaw)
        };

        var isContract = !string.IsNullOrEmpty(inputData) && inputData != "0x";

        var status = statusRaw switch
        {
            1 or 1UL or (byte)1 => "Success",
            0 or 0UL or (byte)0 => "Failed",
            _ => statusRaw?.ToString() == "1" ? "Success" : "Pending"
        };

        // blocks.timestamp is UInt32 unix epoch
        var timestamp = timestampRaw switch
        {
            uint u => DateTimeOffset.FromUnixTimeSeconds(u),
            ulong ul => DateTimeOffset.FromUnixTimeSeconds((long)ul),
            int i => DateTimeOffset.FromUnixTimeSeconds(i),
            long l => DateTimeOffset.FromUnixTimeSeconds(l),
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            _ => long.TryParse(timestampRaw?.ToString(), out var ts)
                ? DateTimeOffset.FromUnixTimeSeconds(ts)
                : DateTimeOffset.UtcNow
        };

        return new TransactionInfo(
            Hash: hash,
            From: from,
            To: to,
            ValueEth: Math.Round(valueEth, 6),
            TokenSymbol: "",
            TokenAmount: 0m,
            IsContractInteraction: isContract,
            ContractName: null,
            Timestamp: timestamp,
            Status: status,
            InputData: inputData
        );
    }

    private static decimal ConvertToEth(object? value)
    {
        if (value == null) return 0m;

        try
        {
            // ClickHouse UInt256 comes as BigInteger or string
            if (value is BigInteger bi)
            {
                var (whole, remainder) = BigInteger.DivRem(bi, BigInteger.Pow(10, 18));
                return (decimal)whole + (decimal)remainder / 1_000_000_000_000_000_000m;
            }

            // Try parsing as decimal directly (already in ETH)
            if (value is decimal d) return d;

            // String representation
            var str = value.ToString();
            if (string.IsNullOrEmpty(str)) return 0m;

            if (BigInteger.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                var (whole2, remainder2) = BigInteger.DivRem(parsed, BigInteger.Pow(10, 18));
                return (decimal)whole2 + (decimal)remainder2 / 1_000_000_000_000_000_000m;
            }

            return decimal.TryParse(str, CultureInfo.InvariantCulture, out var dec) ? dec : 0m;
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }
}
