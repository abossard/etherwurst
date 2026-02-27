using HazMeBeenScammed.Core.Domain;
using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Core.Services;

/// <summary>
/// Traverses wallet-to-wallet transactions and aggregates them into a graph structure.
/// </summary>
public sealed class WalletGraphService(IBlockchainAnalyticsPort blockchain) : IWalletGraphPort
{
    public async Task<WalletGraphResult> BuildGraphAsync(
        WalletGraphQuery query,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeAddress(query.Root.Value);
        var boundedDepth = Math.Clamp(query.Depth, 1, 10);
        var boundedMaxNodes = Math.Clamp(query.MaxNodes, 10, 5000);
        var boundedMaxEdges = Math.Clamp(query.MaxEdges, 10, 10000);
        var boundedLookbackDays = Math.Clamp(query.LookbackDays, 1, 3650);
        var minTime = DateTimeOffset.UtcNow.AddDays(-boundedLookbackDays);

        var nodeStats = new Dictionary<string, NodeAccumulator>(StringComparer.OrdinalIgnoreCase);
        var edgeStats = new Dictionary<string, EdgeAccumulator>(StringComparer.OrdinalIgnoreCase);
        var visitedDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string wallet, int depth)>();

        nodeStats[root] = new NodeAccumulator(root, isSeed: true);
        visitedDepth[root] = 0;
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();

            if (depth >= boundedDepth)
            {
                continue;
            }

            await foreach (var tx in blockchain.GetTransactionsForWalletAsync(new WalletAddress(current), cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tx.Timestamp < minTime || tx.ValueEth < query.MinValueEth)
                {
                    continue;
                }

                var normalizedFrom = NormalizeAddress(tx.From);
                var normalizedTo = NormalizeAddress(tx.To);

                if (string.IsNullOrWhiteSpace(normalizedFrom) || string.IsNullOrWhiteSpace(normalizedTo))
                {
                    continue;
                }

                if (!ShouldInclude(tx, current, query.Direction, normalizedFrom, normalizedTo))
                {
                    continue;
                }

                if (nodeStats.Count >= boundedMaxNodes &&
                    !nodeStats.ContainsKey(normalizedFrom) &&
                    !nodeStats.ContainsKey(normalizedTo))
                {
                    continue;
                }

                var fromNode = GetOrAddNode(nodeStats, normalizedFrom, isSeed: normalizedFrom == root);
                var toNode = GetOrAddNode(nodeStats, normalizedTo, isSeed: normalizedTo == root);

                fromNode.OutboundCount++;
                fromNode.TotalOutEth += tx.ValueEth;
                toNode.InboundCount++;
                toNode.TotalInEth += tx.ValueEth;

                if (tx.IsContractInteraction)
                {
                    toNode.IsContract = true;
                }

                var edgeKey = $"{normalizedFrom}|{normalizedTo}";
                if (!edgeStats.TryGetValue(edgeKey, out var edge))
                {
                    if (edgeStats.Count >= boundedMaxEdges)
                    {
                        continue;
                    }

                    edge = new EdgeAccumulator(normalizedFrom, normalizedTo, tx.Timestamp);
                    edgeStats[edgeKey] = edge;
                }

                edge.TransactionCount++;
                edge.TotalValueEth += tx.ValueEth;
                edge.FirstSeen = edge.FirstSeen > tx.Timestamp ? tx.Timestamp : edge.FirstSeen;
                edge.LastSeen = edge.LastSeen < tx.Timestamp ? tx.Timestamp : edge.LastSeen;
                if (!string.IsNullOrWhiteSpace(tx.TokenSymbol))
                {
                    edge.TokenCounts.TryGetValue(tx.TokenSymbol, out var currentCount);
                    edge.TokenCounts[tx.TokenSymbol] = currentCount + 1;
                }

                TryEnqueueNeighbor(visitedDepth, queue, normalizedFrom, current, depth, boundedDepth);
                TryEnqueueNeighbor(visitedDepth, queue, normalizedTo, current, depth, boundedDepth);
            }
        }

        var nodes = nodeStats.Values
            .OrderByDescending(n => n.IsSeed)
            .ThenByDescending(n => n.InboundCount + n.OutboundCount)
            .Select(n => new WalletGraphNode(
                Address: n.Address,
                Label: ShortLabel(n.Address),
                IsSeed: n.IsSeed,
                IsContract: n.IsContract,
                InboundCount: n.InboundCount,
                OutboundCount: n.OutboundCount,
                TotalInEth: decimal.Round(n.TotalInEth, 6),
                TotalOutEth: decimal.Round(n.TotalOutEth, 6)))
            .ToList();

        var edges = edgeStats.Values
            .OrderByDescending(e => e.TotalValueEth)
            .Select(e => new WalletGraphEdge(
                Id: $"{e.From}->{e.To}",
                From: e.From,
                To: e.To,
                TotalValueEth: decimal.Round(e.TotalValueEth, 6),
                TransactionCount: e.TransactionCount,
                FirstSeen: e.FirstSeen,
                LastSeen: e.LastSeen,
                DominantToken: GetDominantToken(e.TokenCounts)))
            .ToList();

        return new WalletGraphResult(
            Root: root,
            Depth: boundedDepth,
            Direction: query.Direction,
            NodeCount: nodes.Count,
            EdgeCount: edges.Count,
            Nodes: nodes,
            Edges: edges);
    }

    private static bool ShouldInclude(
        TransactionInfo tx,
        string currentWallet,
        GraphDirection direction,
        string normalizedFrom,
        string normalizedTo)
    {
        var isOutgoing = normalizedFrom.Equals(currentWallet, StringComparison.OrdinalIgnoreCase);
        var isIncoming = normalizedTo.Equals(currentWallet, StringComparison.OrdinalIgnoreCase);

        return direction switch
        {
            GraphDirection.Outgoing => isOutgoing,
            GraphDirection.Incoming => isIncoming,
            _ => isOutgoing || isIncoming
        };
    }

    private static void TryEnqueueNeighbor(
        Dictionary<string, int> visitedDepth,
        Queue<(string wallet, int depth)> queue,
        string candidate,
        string current,
        int currentDepth,
        int maxDepth)
    {
        if (candidate.Equals(current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var nextDepth = currentDepth + 1;
        if (nextDepth > maxDepth)
        {
            return;
        }

        if (visitedDepth.TryGetValue(candidate, out var bestDepth) && bestDepth <= nextDepth)
        {
            return;
        }

        visitedDepth[candidate] = nextDepth;
        queue.Enqueue((candidate, nextDepth));
    }

    private static NodeAccumulator GetOrAddNode(Dictionary<string, NodeAccumulator> stats, string address, bool isSeed)
    {
        if (!stats.TryGetValue(address, out var node))
        {
            node = new NodeAccumulator(address, isSeed);
            stats[address] = node;
        }

        if (isSeed)
        {
            node.IsSeed = true;
        }

        return node;
    }

    private static string NormalizeAddress(string address) =>
        string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim().ToLowerInvariant();

    private static string ShortLabel(string address) =>
        address.Length > 10 ? $"{address[..6]}...{address[^4..]}" : address;

    private static string GetDominantToken(Dictionary<string, int> tokenCounts)
    {
        if (tokenCounts.Count == 0)
        {
            return "ETH";
        }

        return tokenCounts.OrderByDescending(kv => kv.Value).First().Key;
    }

    private sealed class NodeAccumulator(string address, bool isSeed)
    {
        public string Address { get; } = address;
        public bool IsSeed { get; set; } = isSeed;
        public bool IsContract { get; set; }
        public int InboundCount { get; set; }
        public int OutboundCount { get; set; }
        public decimal TotalInEth { get; set; }
        public decimal TotalOutEth { get; set; }
    }

    private sealed class EdgeAccumulator(string from, string to, DateTimeOffset initialTimestamp)
    {
        public string From { get; } = from;
        public string To { get; } = to;
        public int TransactionCount { get; set; }
        public decimal TotalValueEth { get; set; }
        public DateTimeOffset FirstSeen { get; set; } = initialTimestamp;
        public DateTimeOffset LastSeen { get; set; } = initialTimestamp;
        public Dictionary<string, int> TokenCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
