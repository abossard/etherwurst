# Logging & Cost Control

This project uses Azure Monitor Container Insights to collect logs from AKS.
**Uncontrolled log ingestion is the #1 surprise cost** — a single chatty pod
can generate 50+ GB/week at ~$2.76/GB.

## Current Setup

| Layer | What | Where |
|-------|------|-------|
| **ConfigMap** | `container-azm-ms-agentconfig` in `kube-system` | `clusters/etherwurst/infrastructure/container-log-config.yaml` |
| **Bicep DCR** | Container Insights Data Collection Rule | `infra/main.bicep` (`dcrContainerInsights`) |
| **Workspace** | Log Analytics, PerGB2018 SKU, 30-day retention | `infra/main.bicep` (`logAnalytics`) |

## What Gets Collected

**Only the `ethereum` namespace** is included in log collection. All other
namespaces are excluded:

- ❌ `blockscout` — block explorer, produces 34 GB/week of stdout
- ❌ `kube-system` — system components, high volume, low value
- ❌ `monitoring`, `flux-system`, `cert-manager`, `nginx-gateway`, `goldilocks`
- ✅ `ethereum` — your app code, ETL jobs, ~100 MB/week total

### Why This Matters

Before filtering was applied, the cluster was ingesting **57.5 GB/week**
(~$636/month) — 94% from ContainerLog alone. After filtering: **~0.1 GB/week**
(~$1/month).

## How to Check Current Ingestion

```kql
// Run in Log Analytics → Logs
Usage
| where TimeGenerated > ago(7d)
| summarize TotalGB = sum(Quantity) / 1024.0 by DataType
| order by TotalGB desc
```

To find which pods are noisiest:

```kql
KubePodInventory
| where TimeGenerated > ago(7d) and Namespace == "ethereum"
| distinct Name, ContainerID
| join kind=inner (
    ContainerLog
    | where TimeGenerated > ago(7d)
    | summarize SizeMB = round(sum(string_size(LogEntry)) / 1048576.0, 1),
                LogCount = count()
      by ContainerID
) on ContainerID
| summarize SizeMB = round(sum(SizeMB), 1) by PodName = Name
| order by SizeMB desc
```

## Adding a New Namespace

If you deploy a new app in a different namespace and want its logs collected,
update **both** locations:

### 1. ConfigMap (immediate effect)

Edit `clusters/etherwurst/infrastructure/container-log-config.yaml`.
The ConfigMap uses an **exclude list** — remove your namespace from the
`exclude_namespaces` arrays under both `stdout` and `stderr` sections.

### 2. Bicep DCR (next `azd provision`)

Edit `infra/main.bicep`, find `dcrContainerInsights`, and add your namespace
to the `namespaces` array:

```bicep
namespaceFilteringMode: 'Include'
namespaces: ['ethereum', 'my-new-namespace']
```

## Guidelines for Application Logging

### Do

- **Log at appropriate levels** — use `Information` for business events,
  `Warning` for recoverable issues, `Error` for failures
- **Use structured logging** — key-value pairs, not free-form text
- **Be concise** — one line per event, not multi-line stack dumps for expected errors
- **Test log volume locally** — run your app for 10 minutes and check `docker logs | wc -l`

### Don't

- ❌ **Log every HTTP request body/response** — use sampling or traces instead
- ❌ **Log in tight loops** — a loop processing 1M items should not log each one
- ❌ **Leave debug logging on** — `ASPNETCORE_ENVIRONMENT=Development` enables
  verbose EF Core query logging, gRPC frame logging, etc.
- ❌ **Log secrets, tokens, or PII** — these end up in Log Analytics and are hard
  to purge (purge requests take up to 30 days)

### Cost Rules of Thumb

| Log rate | Weekly volume | Monthly cost |
|----------|--------------|--------------|
| 1 line/sec (100 bytes) | ~60 MB | ~$0.70 |
| 10 lines/sec | ~600 MB | ~$7 |
| 100 lines/sec | ~6 GB | ~$70 |
| 1000 lines/sec | ~60 GB | ~$700 |

A single pod logging at 1000 lines/sec costs more than the entire AKS cluster.

## Disaster Recovery

If you notice a sudden cost spike:

1. **Find the culprit:**
   ```bash
   az monitor log-analytics query -w <workspace-id> \
     --analytics-query "ContainerLog | where TimeGenerated > ago(1d) | summarize GB=sum(string_size(LogEntry))/1073741824.0 by Image | top 5 by GB"
   ```

2. **Quick fix — add to exclude list:**
   Edit `container-log-config.yaml`, add the namespace to `exclude_namespaces`,
   commit and push. Flux applies it within minutes.

3. **Permanent fix — reduce log verbosity** in the offending application.

## Architecture

```
Pod stdout/stderr
    → AMA agent (DaemonSet in kube-system)
    → Filtered by ConfigMap (namespace exclude list)
    → Filtered by DCR (namespace include list)
    → Log Analytics Workspace (ContainerLogV2 table)
```

The ConfigMap and DCR are defense-in-depth — both filter, so if one is
misconfigured the other still blocks unwanted logs.
