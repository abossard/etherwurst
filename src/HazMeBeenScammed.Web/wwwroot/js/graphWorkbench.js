import Graph from "https://esm.sh/graphology@0.25.4";
import Sigma from "https://esm.sh/sigma@3.0.0-beta.25";

const instances = new Map();

function pick(obj, camel, pascal) {
  return obj?.[camel] ?? obj?.[pascal];
}

function colorWithOpacity(hex, opacity) {
  const value = Math.max(0, Math.min(1, Number(opacity ?? 0.6)));
  const normalized = hex.replace("#", "");
  const bigint = Number.parseInt(normalized, 16);
  const r = (bigint >> 16) & 255;
  const g = (bigint >> 8) & 255;
  const b = bigint & 255;
  return `rgba(${r}, ${g}, ${b}, ${value.toFixed(2)})`;
}

function shortAddress(value) {
  return value.length > 10 ? `${value.slice(0, 6)}...${value.slice(-4)}` : value;
}

function calculateDepths(root, edges) {
  const adjacency = new Map();
  for (const edge of edges) {
    if (!adjacency.has(edge.from)) adjacency.set(edge.from, []);
    if (!adjacency.has(edge.to)) adjacency.set(edge.to, []);
    adjacency.get(edge.from).push(edge.to);
    adjacency.get(edge.to).push(edge.from);
  }

  const depths = new Map([[root, 0]]);
  const queue = [root];

  while (queue.length > 0) {
    const current = queue.shift();
    const depth = depths.get(current);
    const neighbors = adjacency.get(current) || [];
    for (const neighbor of neighbors) {
      if (!depths.has(neighbor)) {
        depths.set(neighbor, depth + 1);
        queue.push(neighbor);
      }
    }
  }

  return depths;
}

function applyLayout(graph, nodes, root, layoutName) {
  const count = Math.max(1, nodes.length);

  if (layoutName === "grid") {
    const cols = Math.ceil(Math.sqrt(count));
    nodes.forEach((node, index) => {
      const row = Math.floor(index / cols);
      const col = index % cols;
      graph.setNodeAttribute(node.address, "x", col * 8);
      graph.setNodeAttribute(node.address, "y", row * 8);
    });
    return;
  }

  if (layoutName === "force") {
    nodes.forEach((node, index) => {
      const angle = (2 * Math.PI * index) / count;
      const radius = 5 + (index % 12) * 1.3;
      const jitter = (index % 5) * 0.4;
      graph.setNodeAttribute(node.address, "x", Math.cos(angle) * radius + jitter);
      graph.setNodeAttribute(node.address, "y", Math.sin(angle) * radius + jitter);
    });
    return;
  }

  if (layoutName === "radial") {
    const radialDepths = calculateDepths(root, Array.from(graph.edges()).map((edgeKey) => {
      const source = graph.source(edgeKey);
      const target = graph.target(edgeKey);
      return { from: source, to: target };
    }));

    const buckets = new Map();
    for (const node of nodes) {
      const depth = radialDepths.get(node.address) ?? 0;
      if (!buckets.has(depth)) {
        buckets.set(depth, []);
      }
      buckets.get(depth).push(node);
    }

    for (const [depth, bucket] of buckets.entries()) {
      const radius = depth * 12;
      if (depth === 0) {
        graph.setNodeAttribute(bucket[0].address, "x", 0);
        graph.setNodeAttribute(bucket[0].address, "y", 0);
        continue;
      }

      bucket.forEach((node, index) => {
        const angle = (2 * Math.PI * index) / bucket.length;
        graph.setNodeAttribute(node.address, "x", Math.cos(angle) * radius);
        graph.setNodeAttribute(node.address, "y", Math.sin(angle) * radius);
      });
    }
    return;
  }

  // Circular fallback.
  nodes.forEach((node, index) => {
    const angle = (2 * Math.PI * index) / count;
    graph.setNodeAttribute(node.address, "x", Math.cos(angle) * 24);
    graph.setNodeAttribute(node.address, "y", Math.sin(angle) * 24);
  });
}

function buildGraphPayload(payload, options) {
  const root = pick(payload, "root", "Root");
  const nodes = pick(payload, "nodes", "Nodes") || [];
  const edges = pick(payload, "edges", "Edges") || [];

  const graph = new Graph({ type: "directed", multi: false });
  const nodeScale = Number(options.nodeSizeScale ?? 1);
  const edgeOpacity = Number(options.edgeOpacity ?? 0.6);
  const labelMode = options.labelMode ?? "smart";

  for (const node of nodes) {
    const address = pick(node, "address", "Address");
    const inboundCount = Number(pick(node, "inboundCount", "InboundCount") ?? 0);
    const outboundCount = Number(pick(node, "outboundCount", "OutboundCount") ?? 0);
    const isSeed = Boolean(pick(node, "isSeed", "IsSeed"));
    const isContract = Boolean(pick(node, "isContract", "IsContract"));
    const activity = Math.max(1, inboundCount + outboundCount);
    const baseSize = Math.min(16, Math.max(2.5, Math.log2(activity + 1) * 2.2));
    const color = isSeed
      ? "#f97316"
      : isContract
        ? "#dc2626"
        : "#0f766e";

    graph.addNode(address, {
      label: labelMode === "none" ? "" : (labelMode === "all" ? address : shortAddress(address)),
      size: baseSize * nodeScale,
      color,
      isSeed,
      activity,
      nodeData: node,
      x: 0,
      y: 0
    });
  }

  for (const edge of edges) {
    const from = pick(edge, "from", "From");
    const to = pick(edge, "to", "To");
    const txCount = Number(pick(edge, "transactionCount", "TransactionCount") ?? 0);
    const totalValueEth = Number(pick(edge, "totalValueEth", "TotalValueEth") ?? 0);

    if (!graph.hasNode(from) || !graph.hasNode(to)) {
      continue;
    }

    const size = Math.min(5, Math.max(0.7, Math.log2(totalValueEth + 1) * 0.8));
    graph.addEdge(from, to, {
      label: `${txCount} tx | ${totalValueEth.toFixed(3)} ETH`,
      size,
      color: colorWithOpacity("#334155", edgeOpacity),
      type: "arrow",
      edgeData: edge
    });
  }

  applyLayout(graph, nodes.map((n) => ({ address: pick(n, "address", "Address") })), root, options.layout ?? "radial");
  return graph;
}

export function initializeGraphWorkbench(containerId) {
  const container = document.getElementById(containerId);
  if (!container) {
    return;
  }

  if (instances.has(containerId)) {
    disposeGraphWorkbench(containerId);
  }

  const graph = new Graph({ type: "directed", multi: false });
  const renderer = new Sigma(graph, container, {
    renderEdgeLabels: false,
    labelDensity: 0.06,
    labelGridCellSize: 80,
    zIndex: true,
    allowInvalidContainer: true
  });

  instances.set(containerId, { graph, renderer, container });
}

export function renderWalletGraph(containerId, payload, options) {
  const instance = instances.get(containerId);
  const nodes = pick(payload, "nodes", "Nodes") || [];
  if (!instance || !payload || !Array.isArray(nodes) || nodes.length === 0) {
    return;
  }

  const { renderer, container } = instance;

  const graph = buildGraphPayload(payload, options ?? {});
  renderer.setGraph(graph);

  const showLabels = (options?.labelMode ?? "smart") !== "none";
  renderer.setSetting("renderLabels", showLabels);
  renderer.setSetting("labelDensity", options?.labelMode === "all" ? 0.18 : 0.06);

  const camera = renderer.getCamera();
  camera.setState({ x: 0.5, y: 0.5, ratio: 1.2 });

  instances.set(containerId, { graph, renderer, container });
}

export function fitGraph(containerId) {
  const instance = instances.get(containerId);
  if (!instance) {
    return;
  }

  const camera = instance.renderer.getCamera();
  camera.animate({ x: 0.5, y: 0.5, ratio: 1.2 }, { duration: 350 });
}

export function downloadGraphScreenshot(containerId) {
  const instance = instances.get(containerId);
  if (!instance) {
    return;
  }

  const canvas = instance.container.querySelector("canvas");
  if (!canvas) {
    return;
  }

  const link = document.createElement("a");
  link.href = canvas.toDataURL("image/png");
  link.download = `wallet-graph-${Date.now()}.png`;
  link.click();
}

export function disposeGraphWorkbench(containerId) {
  const instance = instances.get(containerId);
  if (!instance) {
    return;
  }

  instance.renderer.kill();
  instances.delete(containerId);
}
