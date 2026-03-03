using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// Real implementation of IBlockchainAnalyticsPort backed by Erigon JSON-RPC
/// and Blockscout REST API for contract metadata.
/// </summary>
public sealed class ErigonBlockchainAdapter : IBlockchainAnalyticsPort
{
    private readonly HttpClient _rpcClient;
    private readonly HttpClient _blockscoutClient;
    private readonly ILogger<ErigonBlockchainAdapter> _logger;

    private const string Eip1967ImplementationSlot = "0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ErigonBlockchainAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<ErigonBlockchainAdapter> logger)
    {
        _rpcClient = httpClientFactory.CreateClient("erigon-rpc");
        _blockscoutClient = httpClientFactory.CreateClient("blockscout");
        _logger = logger;
    }

    // ─── IBlockchainAnalyticsPort ────────────────────────────────────

    public async IAsyncEnumerable<TransactionInfo> GetWalletActivityAsync(
        WalletAddress address,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching transactions for wallet {Address}", address.Value);

        const int PageSize = 25;
        const int MaxTransactions = 666;

        var pageToken = 0UL;
        var totalYielded = 0;
        var blockTimestamps = new Dictionary<string, DateTimeOffset>();

        while (totalYielded < MaxTransactions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RpcCall<OtsSearchResult>(
                "ots_searchTransactionsBefore",
                [address.Value, pageToken, PageSize],
                cancellationToken);

            if (result?.Txs == null || result.Txs.Length == 0)
            {
                _logger.LogInformation("No more transactions for {Address} (yielded {Count})", address.Value, totalYielded);
                break;
            }

            _logger.LogInformation("Page returned {Count} txs for {Address} (firstPage={First}, lastPage={Last})",
                result.Txs.Length, address.Value, result.FirstPage, result.LastPage);

            foreach (var bn in result.Txs.Select(t => t.BlockNumber).Where(b => b != null && !blockTimestamps.ContainsKey(b)).Distinct())
            {
                var block = await RpcCall<RpcBlock>(
                    "eth_getBlockByNumber", [bn!, false], cancellationToken);
                if (block?.Timestamp != null)
                    blockTimestamps[bn!] = DateTimeOffset.FromUnixTimeSeconds((long)HexToUlong(block.Timestamp));
            }

            foreach (var tx in result.Txs)
            {
                if (totalYielded >= MaxTransactions) break;

                var receipt = await RpcCall<RpcReceipt>(
                    "eth_getTransactionReceipt", [tx.Hash!], cancellationToken);

                var timestamp = tx.BlockNumber != null && blockTimestamps.TryGetValue(tx.BlockNumber, out var ts)
                    ? ts : DateTimeOffset.UtcNow;

                yield return MapTransaction(tx, receipt, timestamp);
                totalYielded++;
            }

            if (result.LastPage) break;

            if (result.Txs[^1].BlockNumber != null)
                pageToken = HexToUlong(result.Txs[^1].BlockNumber);
            else
                break;
        }

        _logger.LogInformation("Finished fetching {Count} transactions for {Address}", totalYielded, address.Value);
    }

    public async Task<TransactionDetail?> GetTransactionDetailAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching transaction detail {Hash}", hash.Value);

        var tx = await RpcCall<RpcTransaction>(
            "eth_getTransactionByHash", [hash.Value], cancellationToken);

        if (tx == null) return null;

        var receipt = await RpcCall<RpcReceipt>(
            "eth_getTransactionReceipt", [hash.Value], cancellationToken);

        var timestamp = DateTimeOffset.UtcNow;
        if (tx.BlockNumber != null)
        {
            var block = await RpcCall<RpcBlock>(
                "eth_getBlockByNumber", [tx.BlockNumber, false], cancellationToken);
            if (block?.Timestamp != null)
                timestamp = DateTimeOffset.FromUnixTimeSeconds((long)HexToUlong(block.Timestamp));
        }

        var txInfo = MapTransaction(tx, receipt, timestamp);
        var logs = MapLogs(receipt);
        return new TransactionDetail(txInfo, logs);
    }

    public async Task<ContractAssessment?> AssessContractAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Assessing contract at {Address}", address);

        // Check if it's a contract via eth_getCode
        var bytecode = await RpcCall<string>("eth_getCode", [address, "latest"], cancellationToken);
        if (bytecode is null or "0x" or "0x0")
            return null; // EOA, not a contract

        var bytecodeLength = bytecode.Length;

        // Proxy detection via EIP-1967 storage slot
        string? proxyImplementation = null;
        var isProxy = false;
        var implSlot = await RpcCall<string>("eth_getStorageAt", [address, Eip1967ImplementationSlot, "latest"], cancellationToken);
        if (IsLikelyProxyImplementation(implSlot))
        {
            proxyImplementation = ToAddressFromStorageValue(implSlot!);
            // Verify the implementation has code
            var implCode = await RpcCall<string>("eth_getCode", [proxyImplementation, "latest"], cancellationToken);
            isProxy = !string.IsNullOrWhiteSpace(implCode) && implCode != "0x";
            if (!isProxy) proxyImplementation = null;
        }

        // Try Blockscout for rich metadata
        string? name = null;
        var isVerified = false;
        string? abiFragment = null;
        try
        {
            var response = await _blockscoutClient.GetAsync(
                $"/api/v2/smart-contracts/{address}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var sc = await response.Content.ReadFromJsonAsync<BlockscoutSmartContract>(
                    JsonOpts, cancellationToken);

                if (sc != null)
                {
                    name = sc.Name;
                    isVerified = sc.IsVerified ?? false;
                    isProxy = isProxy || (sc.IsProxy ?? false);
                    abiFragment = sc.Abi != null ? TruncateAbi(sc.Abi) : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout metadata lookup failed for {Address}", address);
        }

        return new ContractAssessment(
            Address: address,
            Name: name,
            IsVerified: isVerified,
            IsProxy: isProxy,
            ProxyImplementation: proxyImplementation,
            HasSuspiciouslyShortBytecode: bytecodeLength < 260,
            BytecodeLength: bytecodeLength,
            AbiFragment: abiFragment);
    }

    // ─── JSON-RPC helpers ────────────────────────────────────────────

    private async Task<T?> RpcCall<T>(string method, object[] parameters,
        CancellationToken ct)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = parameters
        };

        var response = await _rpcClient.PostAsJsonAsync("", request, ct);
        response.EnsureSuccessStatusCode();

        var rpcResponse = await response.Content.ReadFromJsonAsync<RpcResponse<T>>(JsonOpts, ct);

        if (rpcResponse?.Error != null)
        {
            throw new InvalidOperationException(
                $"Erigon RPC error {rpcResponse.Error.Code} on {method}: {rpcResponse.Error.Message}");
        }

        return rpcResponse != null ? rpcResponse.Result : default;
    }

    // ─── Mapping ─────────────────────────────────────────────────────

    private static TransactionInfo MapTransaction(RpcTransaction tx, RpcReceipt? receipt, DateTimeOffset timestamp)
    {
        var valueEth = HexToEth(tx.Value ?? "0x0");
        var isContract = !string.IsNullOrEmpty(tx.Input) && tx.Input != "0x";

        var status = receipt?.Status switch
        {
            "0x1" => "Success",
            "0x0" => "Failed",
            _ => "Pending"
        };

        var tokenSymbol = "";
        var tokenAmount = 0m;
        if (receipt?.Logs != null)
        {
            foreach (var log in receipt.Logs)
            {
                if (log.Topics is { Length: >= 3 } &&
                    log.Topics[0] == "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")
                {
                    tokenAmount = HexToTokenAmount(log.Data ?? "0x0");
                    tokenSymbol = "ERC20";
                    break;
                }
            }
        }

        return new TransactionInfo(
            Hash: tx.Hash ?? "",
            From: tx.From ?? "",
            To: tx.To ?? "",
            ValueEth: Math.Round(valueEth, 6),
            TokenSymbol: tokenSymbol,
            TokenAmount: Math.Round(tokenAmount, 4),
            IsContractInteraction: isContract,
            ContractName: null,
            Timestamp: timestamp,
            Status: status,
            InputData: tx.Input
        );
    }

    private static IReadOnlyList<TransactionLogInfo> MapLogs(RpcReceipt? receipt)
    {
        if (receipt?.Logs == null) return [];
        return receipt.Logs.Select(l => new TransactionLogInfo(
            Address: l.Address ?? string.Empty,
            Topics: (l.Topics ?? []).ToList(),
            Data: l.Data ?? "0x")).ToList();
    }

    private static bool IsLikelyProxyImplementation(string? slotValue)
    {
        if (string.IsNullOrWhiteSpace(slotValue) || slotValue == "0x" || slotValue.Length < 10)
            return false;
        var clean = slotValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? slotValue[2..] : slotValue;
        return clean.Trim('0').Length >= 40;
    }

    private static string ToAddressFromStorageValue(string slotValue)
    {
        var clean = slotValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? slotValue[2..] : slotValue;
        if (clean.Length < 40)
            return "0x" + clean.PadLeft(40, '0');
        return "0x" + clean[^40..];
    }

    private string? TruncateAbi(string abi)
    {
        // Return first function signature for display
        try
        {
            using var doc = JsonDocument.Parse(abi);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("type", out var type) &&
                    type.GetString() == "function" &&
                    element.TryGetProperty("name", out var name))
                {
                    return name.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ABI JSON for truncation");
        }
        return null;
    }

    private static ulong HexToUlong(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        return Convert.ToUInt64(hex.StartsWith("0x") ? hex[2..] : hex, 16);
    }

    private static decimal HexToEth(string hex)
    {
        var wei = HexToBigInteger(hex);
        // Divide by 10^18 using BigInteger, then convert the manageable result to decimal
        var (whole, remainder) = BigInteger.DivRem(wei, BigInteger.Pow(10, 18));
        return (decimal)whole + (decimal)remainder / 1_000_000_000_000_000_000m;
    }

    private static decimal HexToTokenAmount(string hex, int decimals = 18)
    {
        var raw = HexToBigInteger(hex);
        var divisor = BigInteger.Pow(10, decimals);
        var (whole, remainder) = BigInteger.DivRem(raw, divisor);
        // Clamp to decimal range for extremely large token supplies
        try { return (decimal)whole + (decimal)remainder / (decimal)divisor; }
        catch (OverflowException) { return decimal.MaxValue; }
    }

    private static BigInteger HexToBigInteger(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex == "0x" || hex == "0x0") return BigInteger.Zero;
        var clean = hex.StartsWith("0x") ? hex[2..] : hex;
        if (clean.Length == 0) return BigInteger.Zero;
        // Prefix with 0 to ensure positive interpretation
        return BigInteger.Parse("0" + clean, NumberStyles.HexNumber);
    }

    // ─── RPC DTOs ────────────────────────────────────────────────────

    private record RpcResponse<T>(
        [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("error")] RpcError? Error
    );

    private record RpcError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message
    );

    private record RpcTransaction(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("from")] string? From,
        [property: JsonPropertyName("to")] string? To,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("input")] string? Input,
        [property: JsonPropertyName("blockNumber")] string? BlockNumber,
        [property: JsonPropertyName("gas")] string? Gas,
        [property: JsonPropertyName("gasPrice")] string? GasPrice
    );

    private record RpcReceipt(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("gasUsed")] string? GasUsed,
        [property: JsonPropertyName("logs")] RpcLog[]? Logs
    );

    private record RpcLog(
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("topics")] string[]? Topics,
        [property: JsonPropertyName("data")] string? Data
    );

    private record OtsSearchResult(
        [property: JsonPropertyName("txs")] RpcTransaction[]? Txs,
        [property: JsonPropertyName("firstPage")] bool FirstPage,
        [property: JsonPropertyName("lastPage")] bool LastPage
    );

    private record RpcBlock(
        [property: JsonPropertyName("timestamp")] string? Timestamp,
        [property: JsonPropertyName("number")] string? Number
    );

    private record BlockscoutSmartContract(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("is_verified")] bool? IsVerified,
        [property: JsonPropertyName("is_proxy")] bool? IsProxy,
        [property: JsonPropertyName("abi")] string? Abi
    );
}
