using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// IBlockchainAnalyticsPort backed by Blockscout REST API v2.
/// Provides enriched data (decoded method names, token transfers, address labels).
/// Uses parallel HTTP fan-out for batch operations.
/// </summary>
public sealed class BlockscoutBlockchainAdapter : IBlockchainAnalyticsPort
{
    private readonly HttpClient _client;
    private readonly IBlockchainAnalyticsPort? _fallback;
    private readonly ILogger<BlockscoutBlockchainAdapter> _logger;

    private const int MaxParallelism = 8;

    public BlockscoutBlockchainAdapter(
        HttpClient client,
        IBlockchainAnalyticsPort? fallback,
        ILogger<BlockscoutBlockchainAdapter> logger)
    {
        _client = client;
        _fallback = fallback;
        _logger = logger;
    }

    // ─── Batch IBlockchainAnalyticsPort ──────────────────────────────

    public IAsyncEnumerable<WalletTransaction> GetWalletActivityAsync(
        IReadOnlyList<WalletAddress> wallets,
        CancellationToken cancellationToken = default)
    {
        if (wallets.Count == 1)
            return GetSingleWalletActivityAsync(wallets[0], cancellationToken);

        return FanOutAsync(wallets, (w, ct) => GetSingleWalletActivityAsync(w, ct), cancellationToken);
    }

    public IAsyncEnumerable<TransactionDetail> GetTransactionDetailsAsync(
        IReadOnlyList<TransactionHash> hashes,
        CancellationToken cancellationToken = default)
    {
        return FanOutAsync(hashes, GetSingleTransactionDetailAsync, cancellationToken);
    }

    public IAsyncEnumerable<ContractAssessment> AssessContractsAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default)
    {
        return FanOutAsync(addresses, AssessSingleContractAsync, cancellationToken);
    }

    // ─── Single-item implementations ─────────────────────────────────

    private async IAsyncEnumerable<WalletTransaction> GetSingleWalletActivityAsync(
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
                yield return new WalletTransaction(address, MapTransaction(tx));
                yielded++;
            }

            if (page.NextPageParams == null)
                break;

            nextPageParams = BuildNextPageQuery(page.NextPageParams);
        }

        _logger.LogInformation("Blockscout: fetched {Count} transactions for {Address}", yielded, address.Value);
    }

    private async IAsyncEnumerable<TransactionDetail> GetSingleTransactionDetailAsync(
        TransactionHash hash,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Blockscout: fetching transaction detail {Hash}", hash.Value);

        TransactionDetail? result = null;
        var useFallback = false;
        try
        {
            var tx = await _client.GetFromJsonAsync<BsTransaction>(
                $"/api/v2/transactions/{hash.Value}", cancellationToken);
            if (tx == null) yield break;

            var txInfo = MapTransaction(tx);

            var logsResponse = await _client.GetFromJsonAsync<BsTransactionLogsResponse>(
                $"/api/v2/transactions/{hash.Value}/logs", cancellationToken);

            var logs = logsResponse?.Items?.Select(l => new TransactionLogInfo(
                Address: l.Address?.Hash ?? "",
                Topics: new List<string>(new[]
                    { l.FirstTopic, l.SecondTopic, l.ThirdTopic, l.FourthTopic }
                    .Where(t => t != null)!),
                Data: l.Data ?? "0x"
            )).ToList() ?? new List<TransactionLogInfo>();

            result = new TransactionDetail(txInfo, logs);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout: failed fetching tx detail {Hash}, trying fallback", hash.Value);
            useFallback = true;
        }

        if (useFallback && _fallback != null)
        {
            await foreach (var d in _fallback.GetTransactionDetailsAsync([hash], cancellationToken))
                yield return d;
            yield break;
        }

        if (result != null)
            yield return result;
    }

    private async IAsyncEnumerable<ContractAssessment> AssessSingleContractAsync(
        string address,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Blockscout: assessing contract {Address}", address);

        ContractAssessment? result = null;
        var useFallback = false;
        try
        {
            var addrInfo = await _client.GetFromJsonAsync<BsAddress>(
                $"/api/v2/addresses/{address}", cancellationToken);

            if (addrInfo == null || !addrInfo.IsContract) yield break;

            BsSmartContract? sc = null;
            try
            {
                sc = await _client.GetFromJsonAsync<BsSmartContract>(
                    $"/api/v2/smart-contracts/{address}", cancellationToken);
            }
            catch { /* unverified contracts may 404 */ }

            result = new ContractAssessment(
                Address: address,
                Name: sc?.Name ?? addrInfo.Name,
                IsVerified: sc?.IsVerified ?? false,
                IsProxy: sc?.IsProxy ?? false,
                ProxyImplementation: null,
                HasSuspiciouslyShortBytecode: false,
                BytecodeLength: 0,
                AbiFragment: sc?.Abi != null ? TruncateAbi(sc.Abi) : null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blockscout: contract assessment failed for {Address}, trying fallback", address);
            useFallback = true;
        }

        if (useFallback && _fallback != null)
        {
            await foreach (var a in _fallback.AssessContractsAsync([address], cancellationToken))
                yield return a;
            yield break;
        }

        if (result != null)
            yield return result;
    }

    // ─── Parallel fan-out ────────────────────────────────────────────

    private static async IAsyncEnumerable<TResult> FanOutAsync<TInput, TResult>(
        IReadOnlyList<TInput> inputs,
        Func<TInput, CancellationToken, IAsyncEnumerable<TResult>> producer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0) yield break;

        var channel = Channel.CreateBounded<TResult>(new BoundedChannelOptions(64)
        {
            SingleWriter = false, SingleReader = true
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(inputs,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
                    async (input, ct) =>
                    {
                        await foreach (var item in producer(input, ct))
                            await channel.Writer.WriteAsync(item, ct);
                    });
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            yield return item;

        await producerTask;
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
