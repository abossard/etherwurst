using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// IBlockchainAnalyticsPort backed by Blockscout REST API v2.
/// Provides enriched data (decoded method names, token transfers, address labels).
/// Falls back to Erigon for live-state operations only when needed.
/// </summary>
public sealed class BlockscoutBlockchainAdapter : IBlockchainAnalyticsPort
{
    private readonly HttpClient _client;
    private readonly IBlockchainAnalyticsPort? _fallback;
    private readonly ILogger<BlockscoutBlockchainAdapter> _logger;

    public BlockscoutBlockchainAdapter(
        HttpClient client,
        IBlockchainAnalyticsPort? fallback,
        ILogger<BlockscoutBlockchainAdapter> logger)
    {
        _client = client;
        _fallback = fallback;
        _logger = logger;
    }

    // ─── IBlockchainAnalyticsPort ────────────────────────────────────

    public async IAsyncEnumerable<TransactionInfo> GetWalletActivityAsync(
        WalletAddress address,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Blockscout: fetching transactions for {Address}", address.Value);

        const int MaxTransactions = 666;
        var yielded = 0;
        string? nextPageParams = null;

        while (yielded < MaxTransactions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"/api/v2/addresses/{address.Value}/transactions";
            if (nextPageParams != null)
                url += "?" + nextPageParams;

            BsAddressTransactionsResponse? page;
            try
            {
                page = await _client.GetFromJsonAsync<BsAddressTransactionsResponse>(url, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Blockscout: failed fetching page for {Address}", address.Value);
                break;
            }

            if (page?.Items == null || page.Items.Count == 0)
                break;

            foreach (var tx in page.Items)
            {
                if (yielded >= MaxTransactions) break;
                yield return MapTransaction(tx);
                yielded++;
            }

            if (page.NextPageParams == null)
                break;

            nextPageParams = BuildNextPageQuery(page.NextPageParams);
        }

        _logger.LogInformation("Blockscout: fetched {Count} transactions for {Address}", yielded, address.Value);
    }

    public async Task<TransactionDetail?> GetTransactionDetailAsync(
        TransactionHash hash,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Blockscout: fetching transaction detail {Hash}", hash.Value);

        try
        {
            var tx = await _client.GetFromJsonAsync<BsTransaction>(
                $"/api/v2/transactions/{hash.Value}", cancellationToken);
            if (tx == null) return null;

            var txInfo = MapTransaction(tx);

            // Fetch logs
            var logsResponse = await _client.GetFromJsonAsync<BsTransactionLogsResponse>(
                $"/api/v2/transactions/{hash.Value}/logs", cancellationToken);

            var logs = logsResponse?.Items?.Select(l => new TransactionLogInfo(
                Address: l.Address?.Hash ?? "",
                Topics: new List<string>(new[]
                    { l.FirstTopic, l.SecondTopic, l.ThirdTopic, l.FourthTopic }
                    .Where(t => t != null)!),
                Data: l.Data ?? "0x"
            )).ToList() ?? new List<TransactionLogInfo>();

            return new TransactionDetail(txInfo, logs);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout: failed fetching tx detail {Hash}, trying fallback", hash.Value);
            return _fallback != null
                ? await _fallback.GetTransactionDetailAsync(hash, cancellationToken)
                : null;
        }
    }

    public async Task<ContractAssessment?> AssessContractAsync(
        string address, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Blockscout: assessing contract {Address}", address);

        try
        {
            // Check if it's a contract
            var addrInfo = await _client.GetFromJsonAsync<BsAddress>(
                $"/api/v2/addresses/{address}", cancellationToken);

            if (addrInfo == null || !addrInfo.IsContract)
                return null;

            // Fetch smart contract details — Blockscout already knows proxy/verified/ABI
            BsSmartContract? sc = null;
            try
            {
                sc = await _client.GetFromJsonAsync<BsSmartContract>(
                    $"/api/v2/smart-contracts/{address}", cancellationToken);
            }
            catch { /* smart-contracts endpoint may 404 for unverified */ }

            return new ContractAssessment(
                Address: address,
                Name: sc?.Name ?? addrInfo.Name,
                IsVerified: sc?.IsVerified ?? false,
                IsProxy: sc?.IsProxy ?? false,
                ProxyImplementation: null, // Blockscout doesn't expose impl address directly in v2
                HasSuspiciouslyShortBytecode: false, // Would need fallback for bytecode length
                BytecodeLength: 0,
                AbiFragment: sc?.Abi != null ? TruncateAbi(sc.Abi) : null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout: contract assessment failed for {Address}, trying fallback", address);
            return _fallback != null
                ? await _fallback.AssessContractAsync(address, cancellationToken)
                : null;
        }
    }

    // ─── Mapping ─────────────────────────────────────────────────────

    internal static TransactionInfo MapTransaction(BsTransaction tx)
    {
        var valueEth = 0m;
        if (!string.IsNullOrEmpty(tx.Value) && decimal.TryParse(tx.Value, out var weiVal))
            valueEth = weiVal / 1_000_000_000_000_000_000m;

        var isContract = tx.To?.IsContract ?? false;
        var status = tx.Status switch
        {
            "ok" => "Success",
            "error" => "Failed",
            _ => "Pending"
        };

        // Extract token info from first token transfer if available
        var tokenSymbol = "";
        var tokenAmount = 0m;
        if (tx.TokenTransfers is { Count: > 0 })
        {
            var first = tx.TokenTransfers[0];
            tokenSymbol = first.Token?.Symbol ?? "ERC20";
            if (!string.IsNullOrEmpty(first.Total?.Value) && first.Token?.Decimals != null)
            {
                if (decimal.TryParse(first.Total.Value, out var rawAmount))
                {
                    var decimals = int.TryParse(first.Token.Decimals, out var d) ? d : 18;
                    tokenAmount = rawAmount / (decimal)Math.Pow(10, decimals);
                }
            }
        }

        DateTimeOffset timestamp;
        if (!string.IsNullOrEmpty(tx.Timestamp))
            timestamp = DateTimeOffset.TryParse(tx.Timestamp, out var ts) ? ts : DateTimeOffset.UtcNow;
        else
            timestamp = DateTimeOffset.UtcNow;

        return new TransactionInfo(
            Hash: tx.Hash ?? "",
            From: tx.From?.Hash ?? "",
            To: tx.To?.Hash ?? "",
            ValueEth: Math.Round(valueEth, 6),
            TokenSymbol: tokenSymbol,
            TokenAmount: Math.Round(tokenAmount, 4),
            IsContractInteraction: isContract,
            ContractName: isContract ? (tx.To?.Name ?? tx.DecodedInput?.MethodCall) : null,
            Timestamp: timestamp,
            Status: status,
            InputData: tx.RawInput);
    }

    private static string? TruncateAbi(string abi)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(abi);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("type", out var type) &&
                    type.GetString() == "function" &&
                    element.TryGetProperty("name", out var name))
                    return name.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string BuildNextPageQuery(BsNextPageParams p)
    {
        var parts = new List<string>();
        if (p.BlockNumber.HasValue) parts.Add($"block_number={p.BlockNumber}");
        if (p.Index.HasValue) parts.Add($"index={p.Index}");
        if (p.ItemsCount.HasValue) parts.Add($"items_count={p.ItemsCount}");
        return string.Join("&", parts);
    }

    // ─── Blockscout API v2 DTOs ──────────────────────────────────────

    internal record BsAddressTransactionsResponse(
        [property: JsonPropertyName("items")] List<BsTransaction>? Items,
        [property: JsonPropertyName("next_page_params")] BsNextPageParams? NextPageParams);

    internal record BsNextPageParams(
        [property: JsonPropertyName("block_number")] long? BlockNumber,
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("items_count")] int? ItemsCount);

    internal record BsTransaction(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("from")] BsAddressRef? From,
        [property: JsonPropertyName("to")] BsAddressRef? To,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("timestamp")] string? Timestamp,
        [property: JsonPropertyName("raw_input")] string? RawInput,
        [property: JsonPropertyName("decoded_input")] BsDecodedInput? DecodedInput,
        [property: JsonPropertyName("token_transfers")] List<BsTokenTransfer>? TokenTransfers);

    internal record BsAddressRef(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("is_contract")] bool IsContract);

    internal record BsDecodedInput(
        [property: JsonPropertyName("method_call")] string? MethodCall,
        [property: JsonPropertyName("method_id")] string? MethodId);

    internal record BsTokenTransfer(
        [property: JsonPropertyName("token")] BsToken? Token,
        [property: JsonPropertyName("total")] BsTokenValue? Total,
        [property: JsonPropertyName("from")] BsAddressRef? From,
        [property: JsonPropertyName("to")] BsAddressRef? To);

    internal record BsToken(
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("decimals")] string? Decimals,
        [property: JsonPropertyName("type")] string? Type);

    internal record BsTokenValue(
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("decimals")] string? Decimals);

    internal record BsAddress(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("is_contract")] bool IsContract);

    internal record BsSmartContract(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("is_verified")] bool? IsVerified,
        [property: JsonPropertyName("is_proxy")] bool? IsProxy,
        [property: JsonPropertyName("abi")] string? Abi);

    internal record BsTransactionLogsResponse(
        [property: JsonPropertyName("items")] List<BsLogEntry>? Items);

    internal record BsLogEntry(
        [property: JsonPropertyName("address")] BsAddressRef? Address,
        [property: JsonPropertyName("data")] string? Data,
        [property: JsonPropertyName("first_topic")] string? FirstTopic,
        [property: JsonPropertyName("second_topic")] string? SecondTopic,
        [property: JsonPropertyName("third_topic")] string? ThirdTopic,
        [property: JsonPropertyName("fourth_topic")] string? FourthTopic);
}
