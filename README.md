# Haz Me Been Scammed? 🔍

An example application that uses the Ethereum infrastructure of the Etherwurst project to analyze wallet addresses and transactions for potential scam activity.

## Architecture

This application follows a **hexagonal architecture** (ports and adapters):

```
┌────────────────────────────────────────────────────────┐
│  Blazor Web Frontend (HazMeBeenScammed.Web)            │
│  • Interactive Server-Side rendering                   │
│  • Live streaming via SSE                              │
└────────────────────┬───────────────────────────────────┘
                     │ HTTP SSE
┌────────────────────▼───────────────────────────────────┐
│  ASP.NET Core API (HazMeBeenScammed.Api)               │
│  • GET /api/analyze?input=... → SSE stream             │
│  • GET /health                                         │
└────────────────────┬───────────────────────────────────┘
                     │ IScamAnalysisPort
┌────────────────────▼───────────────────────────────────┐
│  Core Domain (HazMeBeenScammed.Core)                   │
│  • ScamAnalyzer service                                │
│  • Domain models & enums                               │
│  • IBlockchainAnalyticsPort (interface)                │
└────────────────────┬───────────────────────────────────┘
                     │ IBlockchainAnalyticsPort
┌────────────────────▼───────────────────────────────────┐
│  Fake Analytics Adapter (in Api project)               │
│  • FakeBlockchainAnalyticsAdapter                      │
│  • Generates realistic fake transaction data           │
│  Replace with real Etherscan/Blockscout adapter        │
└────────────────────────────────────────────────────────┘
```

## Projects

| Project | Purpose |
|---------|---------|
| `HazMeBeenScammed.AppHost` | .NET Aspire orchestration |
| `HazMeBeenScammed.ServiceDefaults` | Shared service configuration (health, telemetry) |
| `HazMeBeenScammed.Core` | Pure domain logic, models, and port interfaces |
| `HazMeBeenScammed.Api` | ASP.NET Core Web API with fake analytics adapter |
| `HazMeBeenScammed.Web` | Blazor Web App with live SSE streaming |
| `HazMeBeenScammed.Tests` | Unit tests (domain) + integration tests (API) |

## Running Locally

### Using .NET Aspire (recommended)

```bash
cd src/HazMeBeenScammed.AppHost
dotnet run
```

The Aspire dashboard will open at https://localhost:15888 and orchestrate both services.

### Running services individually

```bash
# Start the API backend
cd src/HazMeBeenScammed.Api
dotnet run

# Start the Blazor frontend (in another terminal)
cd src/HazMeBeenScammed.Web
dotnet run
```

Then open http://localhost:5174 in your browser.

## Running Tests

```bash
dotnet test tests/HazMeBeenScammed.Tests/
```

## Features

- **Live analysis streaming**: Results arrive in real-time via Server-Sent Events (SSE)
- **Wallet analysis**: Enter a wallet address to see all transactions and risk assessment
- **Transaction analysis**: Enter a transaction hash to analyze a specific transaction
- **Scam detection patterns**:
  - Unverified smart contracts
  - Rapid token dump pattern
  - Honeypot token detection
  - Fake approval detection
  - Zero-value transfer detection
- **Risk scoring**: 0-100 risk score with verdict (Clean / Suspicious / Likely Scam / Confirmed Scam)

## Extending with Real Data

To connect to a real blockchain, implement `IBlockchainAnalyticsPort`:

```csharp
public class EtherscanAdapter(HttpClient httpClient) : IBlockchainAnalyticsPort
{
    public async IAsyncEnumerable<TransactionInfo> GetTransactionsForWalletAsync(
        WalletAddress address, CancellationToken ct)
    {
        // Call api.etherscan.io/api?module=account&action=txlist&address=...
    }
    // ...
}
```

Then register it in `Program.cs`:
```csharp
builder.Services.AddSingleton<IBlockchainAnalyticsPort, EtherscanAdapter>();
```
