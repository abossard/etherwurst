# Azure Spot VM Price Dashboard — Design Document

## Problem

Azure Spot VMs offer up to 90% savings over on-demand pricing, but comparing prices
across regions, VM families, and eviction risks is painful. Users must navigate the
Azure Portal, understand cryptic SKU names, and manually correlate pricing with
eviction rates. This dashboard makes Spot VM economics **instantly visible**.

## Design Philosophy

This project follows two foundational texts:

- **Grokking Simplicity** (Eric Normand) — strict separation of Data, Calculations, and Actions.
  All domain types are immutable records (Data). Price comparisons and savings math are pure
  functions (Calculations). API calls and database writes are isolated Actions.
- **A Philosophy of Software Design** (John Ousterhout) — deep modules with simple interfaces.
  `PriceDatabase` hides all SQLite complexity behind three methods. `RetailPriceCollector` hides
  pagination, retries, and JSON parsing behind `CollectAsync(regions)`.

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  Blazor Interactive Server (MudBlazor)                     │
│  Dashboard · Compare · Region Picker · Filter Sliders      │
├────────────────────────────────────────────────────────────┤
│  Minimal API  (/api/prices, /api/regions, /api/collect)    │
├────────────────────────────────────────────────────────────┤
│  PriceDatabase (SQLite — deep module)                      │
├────────────────────────────────────────────────────────────┤
│  Collectors (Actions — RetailPrice · EvictionRate)         │
│  └─ Azure Retail Prices API (no auth)                      │
│  └─ Azure Resource Graph (optional, DefaultAzureCredential)│
└────────────────────────────────────────────────────────────┘
```

## Data Sources

### Azure Retail Prices REST API (no authentication required)

- Endpoint: `https://prices.azure.com/api/retail/prices`
- OData filter for Spot VMs: `serviceName eq 'Virtual Machines' and contains(meterName,'Spot')`
- Paginated via `NextPageLink`
- Returns: retailPrice, unitPrice, armRegionName, meterName, skuName, productName
- Reference: https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices

### Azure Resource Graph — Spot Eviction Rates (requires DefaultAzureCredential)

- Endpoint: `POST https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01`
- KQL: `SpotResources | where type =~ 'microsoft.compute/skuspotevictionrate/location'`
- Returns eviction rate ranges per SKU per region (e.g., "5-10" = 5–10% chance/hour)
- Falls back gracefully when no credentials are available
- Reference: https://learn.microsoft.com/en-us/azure/virtual-machines/spot-vms#azure-resource-graph
- Reference: https://learn.microsoft.com/en-us/rest/api/resource-graph/

### VM Spec Resolution

VM specs (vCPUs, memory) are parsed from Azure SKU naming conventions. The naming
pattern `[Family][vCPUs][Suffix] [Version]` reliably encodes hardware specs for 95%+
of SKUs. No additional API call or authentication needed.

- Reference: https://learn.microsoft.com/en-us/azure/virtual-machines/vm-naming-conventions

## User-Friendly Design

Users never see raw SKU names. Instead they see:

| What Azure Shows        | What Dashboard Shows            |
|-------------------------|---------------------------------|
| Standard_D2s_v3         | General Purpose · 2 vCPUs · 8 GB |
| Standard_E4_v4          | Memory Optimized · 4 vCPUs · 32 GB |
| Standard_F8s_v2         | Compute Optimized · 8 vCPUs · 16 GB |
| Standard_NC6s_v3        | GPU · 6 vCPUs · 112 GB          |

Filters use human terms: vCPU count (slider), memory (slider), category (dropdown).

## Database

SQLite — zero-config, single-file, embedded. No separate database service.
Ideal for a read-heavy pricing cache with periodic bulk updates.

- Reference: https://www.sqlite.org/whentouse.html
- .NET package: `Microsoft.Data.Sqlite`

## Technology Stack

| Component          | Choice                          | Why                                    |
|--------------------|---------------------------------|----------------------------------------|
| Runtime            | .NET 10 (Trimmed + ReadyToRun)  | Best startup, small image              |
| API                | Minimal API                     | AOT-friendly, zero ceremony            |
| UI                 | Blazor Interactive Server       | Full interactivity, no WASM download   |
| UI Components      | MudBlazor                       | Material Design, DataGrid, Charts      |
| Database           | SQLite                          | Embedded, fast, zero-config            |
| Serialization      | System.Text.Json Source Gen     | AOT/trim-safe, zero reflection         |
| Auth (optional)    | DefaultAzureCredential          | Works with az login, MI, env vars      |
| Container          | Docker multi-stage build        | Reproducible, minimal runtime image    |

## Key References

### APIs
- Azure Retail Prices API: https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices
- Azure Resource Graph REST API: https://learn.microsoft.com/en-us/rest/api/resource-graph/
- Azure Spot VMs documentation: https://learn.microsoft.com/en-us/azure/virtual-machines/spot-vms
- VM Naming Conventions: https://learn.microsoft.com/en-us/azure/virtual-machines/vm-naming-conventions

### .NET 10
- Native AOT support: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot
- Blazor in .NET 10: https://learn.microsoft.com/en-us/aspnet/core/blazor/
- Minimal API: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis

### Design
- Grokking Simplicity (Eric Normand): https://www.manning.com/books/grokking-simplicity
- A Philosophy of Software Design (John Ousterhout): https://web.stanford.edu/~ouster/cgi-bin/book.php
- MudBlazor components: https://mudblazor.com/

### Azure Identity
- DefaultAzureCredential: https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/
- Azure Identity library: https://learn.microsoft.com/en-us/dotnet/api/azure.identity

## Running Locally

```bash
cd src/SpotPriceDashboard
docker compose up --build
# Open http://localhost:5080
```

For eviction rate data, ensure `az login` is done before starting Docker,
or set `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET` environment
variables in `docker-compose.yml`.
