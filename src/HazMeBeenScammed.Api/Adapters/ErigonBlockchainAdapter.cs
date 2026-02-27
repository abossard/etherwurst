using System.Net.Http.Json;
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

    public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
        WalletAddress address,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching transactions for wallet {Address}", address.Value);

        const int PageSize = 20; // Erigon max is 25, stay safely under
        const int MaxTransactions = 1000;

        // ots_searchTransactionsBefore with block 0 = start from latest, walk backwards
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

            // Batch-fetch block timestamps for new blocks in this page
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

            // lastPage = we've reached the oldest transaction
            if (result.LastPage) break;

            // Next cursor: block number of the last tx in this page
            if (result.Txs[^1].BlockNumber != null)
                pageToken = HexToUlong(result.Txs[^1].BlockNumber);
            else
                break;
        }

        _logger.LogInformation("Finished fetching {Count} transactions for {Address}", totalYielded, address.Value);
    }

    public async Task<TransactionInfo?> GetTransactionAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching transaction {Hash}", hash.Value);

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

        return MapTransaction(tx, receipt, timestamp);
    }

    public async Task<ContractInfo?> GetContractInfoAsync(
        string address, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching contract info for {Address}", address);

        // First check if it's a contract via eth_getCode
        var code = await RpcCall<string>("eth_getCode", [address, "latest"], cancellationToken);
        if (code is null or "0x" or "0x0")
            return null; // EOA, not a contract

        // Try Blockscout for rich metadata
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
                    return new ContractInfo(
                        Address: address,
                        Name: sc.Name,
                        IsVerified: sc.IsVerified ?? false,
                        IsProxy: sc.IsProxy ?? false,
                        AbiFragment: sc.Abi != null ? TruncateAbi(sc.Abi) : null
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout lookup failed for {Address}, falling back", address);
        }

        // Fallback: we know it's a contract but have no metadata
        return new ContractInfo(
            Address: address,
            Name: null,
            IsVerified: false,
            IsProxy: false,
            AbiFragment: null
        );
    }

    public Task<string?> GetBytecodeAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        RpcCall<string>("eth_getCode", [address, "latest"], cancellationToken);

    public Task<string?> GetStorageAtAsync(
        string address,
        string slot,
        CancellationToken cancellationToken = default) =>
        RpcCall<string>("eth_getStorageAt", [address, slot, "latest"], cancellationToken);

    public async Task<TransactionReceiptInfo?> GetTransactionReceiptAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        var receipt = await RpcCall<RpcReceipt>("eth_getTransactionReceipt", [hash.Value], cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        var logs = receipt.Logs?.Select(l => new TransactionLogInfo(
            Address: l.Address ?? string.Empty,
            Topics: (l.Topics ?? []).ToList(),
            Data: l.Data ?? "0x")).ToList() ?? [];

        return new TransactionReceiptInfo(
            TransactionHash: hash.Value,
            Status: receipt.Status ?? "0x0",
            Logs: logs);
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
        var valueWei = HexToBigDecimal(tx.Value ?? "0x0");
        var valueEth = valueWei / 1_000_000_000_000_000_000m;
        var isContract = !string.IsNullOrEmpty(tx.Input) && tx.Input != "0x";

        var status = receipt?.Status switch
        {
            "0x1" => "Success",
            "0x0" => "Failed",
            _ => "Pending"
        };

        // Detect ERC-20 transfers from logs
        var tokenSymbol = "";
        var tokenAmount = 0m;
        if (receipt?.Logs != null)
        {
            foreach (var log in receipt.Logs)
            {
                // ERC-20 Transfer topic: keccak256("Transfer(address,address,uint256)")
                if (log.Topics is { Length: >= 3 } &&
                    log.Topics[0] == "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")
                {
                    var rawAmount = HexToBigDecimal(log.Data ?? "0x0");
                    tokenAmount = rawAmount / 1_000_000_000_000_000_000m; // assume 18 decimals
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
            ContractName: null, // Enriched later by ScamAnalyzer via GetContractInfoAsync
            Timestamp: timestamp,
            Status: status,
            InputData: tx.Input
        );
    }

    private static string? TruncateAbi(string abi)
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
        catch { /* not valid JSON array */ }
        return null;
    }

    private static ulong HexToUlong(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        return Convert.ToUInt64(hex.StartsWith("0x") ? hex[2..] : hex, 16);
    }

    private static decimal HexToBigDecimal(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex == "0x" || hex == "0x0") return 0m;
        var clean = hex.StartsWith("0x") ? hex[2..] : hex;
        if (clean.Length == 0) return 0m;

        // Parse hex string as decimal to handle large values
        decimal result = 0;
        foreach (var c in clean)
        {
            result = result * 16 + c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 0
            };
        }
        return result;
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
