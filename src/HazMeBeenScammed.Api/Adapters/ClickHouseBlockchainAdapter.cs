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
/// that require live node access (bytecode, storage).
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

    public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
        WalletAddress address,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ClickHouse: fetching transactions for {Address}", address.Value);
        var addr = address.Value.ToLowerInvariant();

        const string sql = """
            SELECT
                hash, from_address, to_address, value,
                status, timestamp, input_data, gas_used
            FROM ethereum.transactions
            WHERE from_address = {address:String} OR to_address = {address:String}
            ORDER BY timestamp DESC
            LIMIT 666
            """;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddParameter("address", addr);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return MapFromReader(reader);
        }

        _logger.LogInformation("ClickHouse: finished wallet query for {Address}", address.Value);
    }

    public async Task<TransactionInfo?> GetTransactionAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ClickHouse: fetching transaction {Hash}", hash.Value);

        const string sql = """
            SELECT
                hash, from_address, to_address, value,
                status, timestamp, input_data, gas_used
            FROM ethereum.transactions
            WHERE hash = {hash:String}
            LIMIT 1
            """;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddParameter("hash", hash.Value.ToLowerInvariant());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
            return MapFromReader(reader);

        // Fall through to live node if not indexed yet
        if (_fallback != null)
        {
            _logger.LogInformation("ClickHouse: tx not found, falling back to Erigon for {Hash}", hash.Value);
            return await _fallback.GetTransactionAsync(hash, cancellationToken);
        }

        return null;
    }

    public async Task<ContractInfo?> GetContractInfoAsync(
        string address, CancellationToken cancellationToken = default)
    {
        // Contract metadata is best served by Blockscout/Erigon
        if (_fallback != null)
            return await _fallback.GetContractInfoAsync(address, cancellationToken);

        return null;
    }

    public async Task<string?> GetBytecodeAsync(
        string address, CancellationToken cancellationToken = default)
    {
        // Live node operation — always delegate
        if (_fallback != null)
            return await _fallback.GetBytecodeAsync(address, cancellationToken);

        return null;
    }

    public async Task<string?> GetStorageAtAsync(
        string address, string slot, CancellationToken cancellationToken = default)
    {
        // Live node operation — always delegate
        if (_fallback != null)
            return await _fallback.GetStorageAtAsync(address, slot, cancellationToken);

        return null;
    }

    public async Task<TransactionReceiptInfo?> GetTransactionReceiptAsync(
        TransactionHash hash, CancellationToken cancellationToken = default)
    {
        // Receipts/logs are best served by the live node
        if (_fallback != null)
            return await _fallback.GetTransactionReceiptAsync(hash, cancellationToken);

        return null;
    }

    // ─── Mapping ─────────────────────────────────────────────────────

    private static TransactionInfo MapFromReader(IDataReader reader)
    {
        var hash = reader.GetString(0);
        var from = reader.GetString(1);
        var to = reader.GetString(2);
        var valueRaw = reader.GetValue(3);
        var statusRaw = reader.GetValue(4);
        var timestamp = reader.GetDateTime(5);
        var inputData = reader.IsDBNull(6) ? null : reader.GetString(6);
        var gasUsed = reader.IsDBNull(7) ? 0UL : Convert.ToUInt64(reader.GetValue(7));

        var valueEth = ConvertToEth(valueRaw);
        var isContract = !string.IsNullOrEmpty(inputData) && inputData != "0x";

        var status = statusRaw switch
        {
            1 or 1UL or (byte)1 => "Success",
            0 or 0UL or (byte)0 => "Failed",
            _ => statusRaw?.ToString() == "1" ? "Success" : "Pending"
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
            Timestamp: new DateTimeOffset(timestamp, TimeSpan.Zero),
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
