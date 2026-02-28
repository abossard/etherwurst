# Azure Application Gateway for Containers (AGC)

> Research compiled Feb 2026. AGC is GA; WAF is in **Public Preview**.

## What Is It?

AGC is a **Layer 7 load balancer purpose-built for Kubernetes workloads**. It is the evolution of AGIC (Application Gateway Ingress Controller) but is a completely new product with its own data plane and control plane.

| Aspect | AGIC (legacy) | AGC (new) |
|--------|---------------|-----------|
| Infrastructure | Programs a classic App Gateway v2 | Own dedicated data plane |
| API support | Ingress API only | **Ingress + Gateway API** |
| Config speed | Slow (ARM round-trips) | Near real-time |
| Config method | Annotations | Native CRDs |
| Traffic splitting | No | Yes (weighted round robin) |
| mTLS | Limited | Full (frontend + backend + E2E) |
| gRPC / WebSocket / SSE | Limited | Full support |
| Private IP frontend | Yes | **Not yet supported** |
| Ports | Any | **Only 80 and 443** |
| WAF | App Gateway WAF v2 (GA) | Own WAF integration (**preview**) |
| Certificates | Azure Key Vault direct | **K8s Secrets only** (no KV CSI) |

**Sources:**
- [Overview](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/overview)
- [Components](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/application-gateway-for-containers-components)
- [Migration from AGIC](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/migrate-from-agic-to-agc)

---

## Architecture

```
                    Internet
                       │
              ┌────────▼────────┐
              │    Frontend      │  ← Azure-managed FQDN (CNAME your domain)
              │   (port 80/443) │
              ├─────────────────┤
              │  AGC Data Plane  │  ← Lives in delegated subnet (/24+)
              │  (Association)   │     Microsoft.ServiceNetworking/trafficControllers
              └────────┬────────┘
                       │
              ┌────────▼────────┐
              │  AKS Cluster     │
              │  ┌──────────┐   │
              │  │ALB Ctrl  │   │  ← Watches Gateway/HTTPRoute/CRDs
              │  │(2 pods)  │   │     Pushes config to AGC via ARM
              │  └──────────┘   │
              │  ┌──────────┐   │
              │  │ Your Pods │   │
              │  └──────────┘   │
              └─────────────────┘
```

**Three core Azure resources:**
1. **Application Gateway for Containers** — parent resource (control plane)
2. **Frontend** — entry point, gets a unique FQDN. Multiple per AGC.
3. **Association** — connects AGC to a VNet subnet. Currently **1 per AGC**.

**Two deployment models:**
- **ALB-managed**: ALB Controller creates/manages AGC resources via `ApplicationLoadBalancer` CRD
- **BYO (Bring Your Own)**: You pre-provision AGC in Azure, reference it from K8s via annotations

---

## Installation (AKS Add-on)

```bash
# For a NEW cluster (all-in-one)
az aks create \
    --resource-group $RG --name $AKS_NAME --location $LOCATION \
    --network-plugin azure \
    --enable-oidc-issuer \
    --enable-workload-identity \
    --enable-gateway-api \
    --enable-application-load-balancer \
    --generate-ssh-keys

# For an EXISTING cluster
az aks update -g $RG -n $AKS_NAME \
    --enable-oidc-issuer --enable-workload-identity
az aks update -g $RG -n $AKS_NAME \
    --enable-gateway-api --enable-application-load-balancer

# Verify
kubectl get pods -n kube-system | grep alb-controller
kubectl get gatewayclass azure-alb-external
```

The add-on auto-creates:
- Managed identity `applicationloadbalancer-<cluster>` with required RBAC roles
- Subnet `aks-appgateway` with `Microsoft.ServiceNetworking/TrafficController` delegation

**Source:** [Quickstart: Deploy ALB Controller](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-deploy-application-gateway-for-containers-alb-controller)

---

## Basic Configuration (Gateway API)

### ApplicationLoadBalancer (managed deployment)

```yaml
apiVersion: alb.networking.azure.io/v1
kind: ApplicationLoadBalancer
metadata:
  name: alb-test
  namespace: alb-test-infra
spec:
  associations:
  - /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet-alb>
```

### Gateway + HTTPRoute

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: gateway-01
  namespace: test-infra
  annotations:
    alb.networking.azure.io/alb-namespace: alb-test-infra
    alb.networking.azure.io/alb-name: alb-test
spec:
  gatewayClassName: azure-alb-external
  listeners:
  - name: https-listener
    port: 443
    protocol: HTTPS
    allowedRoutes:
      namespaces:
        from: Same
    tls:
      mode: Terminate
      certificateRefs:
      - kind: Secret
        name: listener-tls-secret
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: app-route
  namespace: test-infra
spec:
  parentRefs:
  - name: gateway-01
  hostnames:
  - "contoso.com"
  rules:
  - backendRefs:
    - name: my-app
      port: 8080
```

### BYO Gateway (reference existing AGC)

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: gateway-01
  annotations:
    alb.networking.azure.io/alb-id: /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ServiceNetworking/trafficControllers/<name>
spec:
  gatewayClassName: azure-alb-external
  listeners:
  - name: https-listener
    port: 443
    protocol: HTTPS
    tls:
      mode: Terminate
      certificateRefs:
      - kind: Secret
        name: listener-tls-secret
  addresses:
  - type: alb.networking.azure.io/alb-frontend
    value: <frontend-name>
```

**Sources:**
- [ALB Managed Quickstart](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-create-application-gateway-for-containers-managed-by-alb-controller)
- [BYO Quickstart](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-create-application-gateway-for-containers-byo-deployment)
- [SSL Offloading](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-ssl-offloading-gateway-api)

---

## WAF (Web Application Firewall) — Public Preview

WAF is configured by referencing an **Azure WAF Policy** from Kubernetes via the `WebApplicationFirewallPolicy` CRD.

### Step 1: Create Azure WAF Policy

```bash
az network application-gateway waf-policy create \
  --name agc-waf --resource-group $RG --type ApplicationGateway
```

Configure in Azure Portal: managed rules (DRS 2.2), custom rules, detection/prevention mode.

### Step 2: Reference from Kubernetes

**Scope to entire Gateway:**
```yaml
apiVersion: alb.networking.azure.io/v1
kind: WebApplicationFirewallPolicy
metadata:
  name: waf-policy
  namespace: test-infra
spec:
  targetRef:
    group: gateway.networking.k8s.io
    kind: Gateway
    name: gateway-01
    namespace: test-infra
  webApplicationFirewall:
    id: /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/applicationGatewayWebApplicationFirewallPolicies/agc-waf
```

**Scope to a specific listener:**
```yaml
spec:
  targetRef:
    kind: Gateway
    name: gateway-01
    sectionNames: ["contoso-listener"]
  webApplicationFirewall:
    id: /subscriptions/.../agc-waf
```

**Scope to an HTTPRoute (per-path):**
```yaml
spec:
  targetRef:
    kind: HTTPRoute
    name: api-route
  webApplicationFirewall:
    id: /subscriptions/.../agc-waf
```

### What's Supported

| Feature | Status |
|---------|--------|
| DRS 2.1 / 2.2 managed rules (OWASP-based) | Supported |
| Bot Manager 1.0 / 1.1 | Supported |
| Custom rules (IP, geo, URI, headers, body) | Supported |
| Rate limiting rules | Supported |
| Detection mode | Supported |
| Prevention mode (403 block) | Supported |

### Limitations

- **No CRS** (Core Rule Set) — only DRS (Default Rule Set)
- **No X-Forwarded-For** variable in custom rules
- **No custom block response** body/code
- **No JavaScript/CAPTCHA** challenge actions on Bot Manager rules
- **No HTTP DDoS Ruleset**
- WAF policy must be in the **same subscription and region** as AGC
- **No Security Copilot** integration

### Pricing Impact

WAF roughly **doubles** the per-meter cost:

| Meter | Standard | With WAF |
|-------|----------|----------|
| AGC-hour | $0.017 | $0.031 |
| Frontend-hour | $0.01 | $0.018 |
| Association-hour | $0.12 | $0.216 |
| CU-hour | $0.008 | $0.014 |

**Sources:**
- [WAF on AGC](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/web-application-firewall)
- [WAF How-To](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-waf-gateway-api)
- [DRS Rule Sets](https://learn.microsoft.com/en-us/azure/web-application-firewall/ag/application-gateway-crs-rulegroups-rules)

---

## Authentication

**AGC has NO native OAuth2/OIDC/JWT authentication.** It does support certificate-based auth.

### What AGC Supports Natively

| Capability | Supported | CRD |
|------------|-----------|-----|
| Frontend mTLS (client cert required) | Yes | `FrontendTLSPolicy` |
| Backend mTLS (AGC → backend) | Yes | `BackendTLSPolicy` |
| End-to-end mTLS | Yes | Both |
| TLS termination | Yes | Gateway `tls.mode: Terminate` |
| TLS policy (cipher suites) | Yes | `FrontendTLSPolicy` |
| Header rewriting | Yes | `RequestHeaderModifier` on HTTPRoute |
| OAuth2 / OIDC / JWT at gateway | **No** | — |
| Entra ID integration | **No** | — |

### Frontend mTLS (Client Certificate Auth)

```yaml
apiVersion: alb.networking.azure.io/v1
kind: FrontendTLSPolicy
metadata:
  name: mtls-policy
  namespace: test-infra
spec:
  targetRef:
    group: gateway.networking.k8s.io
    kind: Gateway
    name: gateway-01
    sectionNames: ["mtls-listener"]
  default:
    verify:
      caCertificateRef:
        name: ca.bundle
        kind: Secret
        namespace: test-infra
```

### Backend mTLS

```yaml
apiVersion: alb.networking.azure.io/v1
kind: BackendTLSPolicy
metadata:
  name: backend-tls
  namespace: test-infra
spec:
  targetRef:
    kind: Service
    name: my-backend
  default:
    sni: backend.internal
    ports:
    - port: 443
    clientCertificateRef:
      name: gateway-client-cert
      kind: Secret
    verify:
      caCertificateRef:
        name: ca.bundle
        kind: Secret
      subjectAltName: backend.internal
```

### TLS Policies

| Policy | Min TLS | Cipher Suites |
|--------|---------|---------------|
| `2023-06` (default) | 1.2 | Includes CBC |
| `2023-06-S` (strict) | 1.2 | GCM-only |

```yaml
apiVersion: alb.networking.azure.io/v1
kind: FrontendTLSPolicy
metadata:
  name: strict-tls
spec:
  targetRef:
    kind: Gateway
    name: gateway-01
    sectionNames: ["https-listener"]
    group: gateway.networking.k8s.io
  default:
    policyType:
      type: predefined
      name: 2023-06-S
```

### Patterns for OAuth2/OIDC Authentication

Since AGC lacks native identity auth, use one of these patterns:

**Pattern A: OAuth2 Proxy (recommended for web apps)**
```
Client → AGC → OAuth2 Proxy (Entra ID) → Backend App
```
Deploy [oauth2-proxy](https://oauth2-proxy.github.io/oauth2-proxy/) as a K8s service, route via HTTPRoute.

**Pattern B: Application-level auth (MSAL)**
Handle OIDC directly in your app using Microsoft Identity libraries.

**Pattern C: Azure Front Door + AGC**
Front Door in front of AGC for additional security headers and caching. (Front Door also lacks native OAuth.)

**Pattern D: Entra Application Proxy (internal apps)**
For internal apps, Entra Application Proxy provides pre-authentication before traffic reaches AGC.

**Sources:**
- [Frontend mTLS](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-frontend-mtls-gateway-api)
- [Backend mTLS](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-backend-mtls-gateway-api)
- [End-to-End TLS](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-end-to-end-tls-gateway-api)
- [TLS Policy](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/tls-policy)
- [cert-manager / Let's Encrypt](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-cert-manager-lets-encrypt-gateway-api)

---

## Advanced Routing

### Path + Header + Query String Routing

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: advanced-route
spec:
  parentRefs:
  - name: gateway-01
  rules:
  - matches:
    - path:
        type: PathPrefix
        value: /api
      headers:
      - type: Exact
        name: x-version
        value: "2"
    backendRefs:
    - name: api-v2
      port: 8080
  - matches:
    - path:
        type: PathPrefix
        value: /api
    backendRefs:
    - name: api-v1
      port: 8080
  - backendRefs:
    - name: frontend
      port: 80
```

### URL Rewrite

```yaml
filters:
- type: URLRewrite
  urlRewrite:
    path:
      type: ReplacePrefixMatch
      replacePrefixMatch: /ecommerce
```

### Header Rewrite

```yaml
filters:
- type: RequestHeaderModifier
  requestHeaderModifier:
    set:
    - name: X-Custom
      value: "injected"
    add:
    - name: X-Request-Source
      value: "agc"
    remove: ["X-Debug"]
```

### Session Affinity (Cookie-Based)

```yaml
apiVersion: alb.networking.azure.io/v1
kind: RoutePolicy
metadata:
  name: sticky-sessions
spec:
  targetRef:
    kind: HTTPRoute
    name: app-route
  default:
    sessionAffinity:
      affinityType: "application-cookie"
      cookieName: "session_id"
      cookieDuration: 3600s
```

### Health Probes

```yaml
apiVersion: alb.networking.azure.io/v1
kind: HealthCheckPolicy
metadata:
  name: health-probe
spec:
  targetRef:
    kind: Service
    name: my-app
  default:
    interval: 5s
    timeout: 3s
    healthyThreshold: 1
    unhealthyThreshold: 3
    http:
      path: /health
      match:
        statusCodes:
        - start: 200
          end: 299
```

**Sources:**
- [Path/Header/Query Routing](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-path-header-query-string-routing-gateway-api)
- [URL Rewrite](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-url-rewrite-gateway-api)
- [Header Rewrite](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-header-rewrite-gateway-api)
- [Session Affinity](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/session-affinity)
- [Health Probes](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/custom-health-probe)

---

## Pricing

No upfront costs. Pure consumption billing.

| Meter | Standard/hr | WAF/hr | Standard/month (730h) |
|-------|-------------|--------|----------------------|
| AGC resource | $0.017 | $0.031 | $12.41 |
| Frontend | $0.010 | $0.018 | $7.30 |
| Association | $0.120 | $0.216 | $87.60 |
| Capacity Unit | $0.008 | $0.014 | per CU |

**Capacity Unit** = max(2,500 connections, 2.22 Mbps throughput)

**Example: minimal deployment** (1 AGC, 1 frontend, 1 association, 2 CUs):
**~$119/month** (standard) or **~$214/month** (with WAF)

**Source:** [Pricing](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/understanding-pricing)

---

## Applying AGC to Etherwurst (checkmyethereum.com)

### Current Setup
- AKS cluster with Azure CNI Overlay + Cilium
- NGINX Gateway API (`gatewayClassName: nginx`)
- HazMeBeenScammed Web (Blazor, port 8080) + API (ASP.NET, port 8080)
- Let's Encrypt via cert-manager
- Azure Front Door (Premium) + WAF already provisioned in Bicep
- VNet with `snet-alb` subnet (10.0.16.0/24) already provisioned
- ALB managed identity (`id-*-alb`) already exists

### What Would Change

1. **Replace NGINX Gateway** with `gatewayClassName: azure-alb-external`
2. **Delegate subnet** `snet-alb` to `Microsoft.ServiceNetworking/trafficControllers`
3. **Install ALB Controller** add-on (or it may already be enabled — check `az aks show`)
4. **Migrate gateway.yaml** from NGINX to AGC format
5. **cert-manager** continues to work (AGC supports it natively)
6. **WAF**: Reference Azure WAF policy via `WebApplicationFirewallPolicy` CRD
7. **Consider**: You already have Azure Front Door + WAF. AGC WAF is preview. You could keep Front Door WAF and use AGC without WAF, or switch entirely to AGC WAF once GA.

### Proposed Gateway Config for Etherwurst

```yaml
apiVersion: alb.networking.azure.io/v1
kind: ApplicationLoadBalancer
metadata:
  name: etherwurst-alb
  namespace: alb-infra
spec:
  associations:
  - /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/snet-alb
---
apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: etherwurst-gateway
  namespace: ethereum
  annotations:
    alb.networking.azure.io/alb-namespace: alb-infra
    alb.networking.azure.io/alb-name: etherwurst-alb
    cert-manager.io/issuer: letsencrypt-prod
spec:
  gatewayClassName: azure-alb-external
  listeners:
  - name: http
    port: 80
    protocol: HTTP
    allowedRoutes:
      namespaces:
        from: Same
  - name: https
    port: 443
    protocol: HTTPS
    hostname: "checkmyethereum.com"
    allowedRoutes:
      namespaces:
        from: Same
    tls:
      mode: Terminate
      certificateRefs:
      - kind: Secret
        name: etherwurst-tls
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: redirect-http
  namespace: ethereum
spec:
  parentRefs:
  - name: etherwurst-gateway
    sectionName: http
  rules:
  - filters:
    - type: RequestRedirect
      requestRedirect:
        scheme: https
        statusCode: 301
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: web-route
  namespace: ethereum
spec:
  parentRefs:
  - name: etherwurst-gateway
    sectionName: https
  hostnames:
  - "checkmyethereum.com"
  rules:
  - backendRefs:
    - name: hazmebeenscammed-web
      port: 80
---
apiVersion: alb.networking.azure.io/v1
kind: HealthCheckPolicy
metadata:
  name: web-health
  namespace: ethereum
spec:
  targetRef:
    kind: Service
    name: hazmebeenscammed-web
  default:
    interval: 5s
    timeout: 3s
    healthyThreshold: 1
    unhealthyThreshold: 3
    http:
      path: /health
      match:
        statusCodes:
        - start: 200
          end: 299
---
# Optional: WAF (preview)
apiVersion: alb.networking.azure.io/v1
kind: WebApplicationFirewallPolicy
metadata:
  name: etherwurst-waf
  namespace: ethereum
spec:
  targetRef:
    group: gateway.networking.k8s.io
    kind: Gateway
    name: etherwurst-gateway
    namespace: ethereum
  webApplicationFirewall:
    id: /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/applicationGatewayWebApplicationFirewallPolicies/<waf-policy>
```

### Key Considerations

| Topic | Decision Point |
|-------|---------------|
| **WAF** | You already have Front Door WAF (GA). AGC WAF is preview. Keep Front Door WAF for now? |
| **Private IP** | AGC does not support private frontend IPs. If you need private ingress, keep NGINX or use Front Door → private link. |
| **SSE support** | AGC supports SSE natively — good for the `/api/analyze` endpoint |
| **Ports** | Only 80/443 — fine for web traffic. Erigon RPC (8545) stays cluster-internal. |
| **Auth** | No native OAuth/OIDC. Use app-level MSAL or add oauth2-proxy for Entra ID gate. |
| **Certificates** | Must use K8s Secrets (cert-manager works). No direct Key Vault reference. |

---

## CRD Reference

| CRD | Purpose |
|-----|---------|
| `ApplicationLoadBalancer` | Declares AGC instance (managed mode) |
| `WebApplicationFirewallPolicy` | Attaches Azure WAF policy to Gateway/Listener/HTTPRoute |
| `FrontendTLSPolicy` | Frontend mTLS + TLS cipher policy |
| `BackendTLSPolicy` | Backend mTLS + certificate validation |
| `HealthCheckPolicy` | Custom health probes |
| `RoutePolicy` | Session affinity (Gateway API) |
| `IngressExtension` | Session affinity + backend settings (Ingress API) |

**Full API spec:** [API Specification](https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/api-specification-kubernetes)

---

## Key Limitations Summary

- **Frontend ports**: Only 80 and 443
- **Private IP**: Not supported (public frontend only)
- **Key Vault certs**: Not supported — must use K8s Secrets
- **Request timeout**: Fixed 60s (not configurable)
- **Connection draining**: Always on (not configurable)
- **WAF**: Preview — no CRS rules, no XFF in custom rules, no custom block response
- **Association**: Max 1 per AGC resource
- **Subnet**: Requires /24+ with `Microsoft.ServiceNetworking/trafficControllers` delegation

---

## All Documentation Links

| Topic | URL |
|-------|-----|
| Overview | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/overview |
| Components | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/application-gateway-for-containers-components |
| ALB Controller Install | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-deploy-application-gateway-for-containers-alb-controller |
| ALB Managed Quickstart | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-create-application-gateway-for-containers-managed-by-alb-controller |
| BYO Quickstart | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/quickstart-create-application-gateway-for-containers-byo-deployment |
| WAF Concepts | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/web-application-firewall |
| WAF How-To | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-waf-gateway-api |
| SSL Offloading | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-ssl-offloading-gateway-api |
| End-to-End TLS | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-end-to-end-tls-gateway-api |
| Frontend mTLS | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-frontend-mtls-gateway-api |
| Backend mTLS | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-backend-mtls-gateway-api |
| TLS Policy | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/tls-policy |
| Path/Header/Query Routing | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-path-header-query-string-routing-gateway-api |
| URL Rewrite | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-url-rewrite-gateway-api |
| Header Rewrite | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-header-rewrite-gateway-api |
| Session Affinity | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/session-affinity |
| Health Probes | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/custom-health-probe |
| cert-manager / Let's Encrypt | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/how-to-cert-manager-lets-encrypt-gateway-api |
| Pricing | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/understanding-pricing |
| Release Notes | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/alb-controller-release-notes |
| Migration from AGIC | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/migrate-from-agic-to-agc |
| API Specification (CRDs) | https://learn.microsoft.com/en-us/azure/application-gateway/for-containers/api-specification-kubernetes |
