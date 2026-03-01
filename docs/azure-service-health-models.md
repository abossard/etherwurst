# Azure Health Models & Service Health Alerts — Bicep

## 1. Azure Monitor Health Models (Preview)

Workload-centric health modeling. Models how resources depend on each other, propagates health state through a dependency graph, and provides a single "is my workload healthy?" view.

**API**: `2025-05-03-preview` — **Preview**, may change before GA.

### Resource Hierarchy

```
Microsoft.Monitor/accounts
  └── healthmodels
        ├── signaldefinitions   (reusable metric/log/PromQL signal definitions)
        └── entities            (nodes in the health graph, with signals & alerts)
```

### Prerequisites

- **Service Group** (`Microsoft.Management/serviceGroups@2024-02-01-preview`, tenant-scoped) — groups resources into a workload
- **Azure Monitor Account** (`Microsoft.Monitor/accounts`) — parent resource for health models

### Key Concepts

| Concept | Description |
|---------|-------------|
| **Entity** | Node in health graph: Root (workload), Azure Resource (auto-discovered), or Generic (manual) |
| **Signal** | Metric/KQL/PromQL query with degraded + unhealthy thresholds → determines entity health |
| **Health State** | Healthy / Degraded / Unhealthy / Unknown — worst signal wins |
| **Propagation** | Child health rolls up to parents. Impact: `Standard`, `Limited`, `Suppressed` |
| **Health Objective** | Target % healthy time (SLO tracking) |

### Signal Kinds

`AzureResourceMetric` (platform metrics), `LogAnalyticsQuery` (KQL), `PrometheusMetricsQuery` (PromQL). Also supports `dynamicDetectionRule` for ML-based anomaly detection instead of static thresholds.

### Bicep References

- [healthmodels](https://learn.microsoft.com/en-us/azure/templates/microsoft.monitor/accounts/healthmodels) — the model resource itself
- [signaldefinitions](https://learn.microsoft.com/en-us/azure/templates/microsoft.monitor/accounts/healthmodels/signaldefinitions) — reusable signal definitions with thresholds
- [entities](https://learn.microsoft.com/en-us/azure/templates/microsoft.monitor/accounts/healthmodels/entities) — graph nodes with signal assignments & alerts
- [Service Groups](https://learn.microsoft.com/en-us/azure/governance/service-groups/manage-membership) — prerequisite, includes Bicep sample

### Potential Model for This Project

```
Root: Etherwurst Workload (99% SLO)
├── AKS Cluster         [Standard]  — CPU, memory, node readiness
├── Erigon              [Standard]  — sync progress, peer count, block lag (PromQL)
├── Lighthouse          [Standard]  — sync status, attestation success (PromQL)
├── Blockscout          [Limited]   — HTTP errors, indexing lag
└── Otterscan           [Suppressed] — availability only
```

---

## 2. Service Health Alerts (GA)

Platform-level alerts for Azure outages, maintenance, and advisories. Uses `Microsoft.Insights/activityLogAlerts` with `category: ServiceHealth` or `ResourceHealth`.

Deploy at subscription scope with `Microsoft.Insights/activityLogAlerts`. Incident types: `Incident`, `Maintenance`, `Informational`, `ActionRequired`. See [Service Health Alerts Bicep guide](https://learn.microsoft.com/en-us/azure/service-health/alerts-activity-log-service-notifications-bicep) and [Quickstart template](https://github.com/Azure/azure-quickstart-templates/blob/master/quickstarts/microsoft.insights/insights-alertrules-servicehealth/main.bicep).

---

## References

- [Health Models Overview](https://learn.microsoft.com/en-us/azure/azure-monitor/health-models/overview) · [Concepts](https://learn.microsoft.com/en-us/azure/azure-monitor/health-models/concepts) · [Create](https://learn.microsoft.com/en-us/azure/azure-monitor/health-models/create) · [Designer](https://learn.microsoft.com/en-us/azure/azure-monitor/health-models/designer)
- [Service Groups](https://learn.microsoft.com/en-us/azure/governance/service-groups/overview)
- [Health Modeling (Well-Architected Framework)](https://learn.microsoft.com/en-us/azure/well-architected/design-guides/health-modeling)
