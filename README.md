# CI/CD Automation for .NET Application Deployment to Minikube/Kubernetes

Sample project for the Significa DevOps Engineer assessment: builds, containerizes, and
deploys a .NET 8 Web API to a Kubernetes cluster via Azure DevOps.

## Repo structure

```
.
├── src/SampleApp/            # ASP.NET Core 8 Web API (sample app)
│   ├── Program.cs            # health/live, health/ready, health/startup endpoints
│   ├── Controllers/
│   └── SampleApp.csproj
├── tests/SampleApp.Tests/     # xUnit: WeatherController unit tests + health-endpoint
│   └── ...                    # integration tests via WebApplicationFactory
├── Dockerfile                 # multi-stage build (sdk -> aspnet runtime), non-root user
├── .dockerignore
├── azure-pipelines.yml         # 3-stage pipeline: Build -> Docker -> Deploy
└── k8s/
    ├── namespace.yaml
    ├── deployment.yaml         # includes liveness/readiness/startup probes
    ├── service.yaml
    └── hpa.yaml                # CPU/memory-based autoscaling (bonus)
```

## Task 1 — Azure DevOps configuration

- New Azure DevOps project + Git repo hosting this code.
- `azure-pipelines.yml` is triggered on commits to `main` (path-filtered to app/docker/k8s
  changes so unrelated commits, e.g. docs, don't trigger a rebuild).
- The `Build` stage runs `dotnet restore/build/test/publish` and publishes the publish output
  as a pipeline artifact (`drop`) — this is the artifact consumed by the Docker build stage.

## Task 2 — Docker image

- The `Dockerfile` uses a **multi-stage build**: `dotnet/sdk:8.0` to restore/publish, then
  copies only the published output into `dotnet/aspnet:8.0` (runtime-only, much smaller image,
  smaller attack surface — no SDK/build tools shipped to production).
- Runs as a **non-root user** (`appuser`).
- Image is tagged with the pipeline's `$(Build.BuildId)` (immutable, traceable back to the
  exact commit/build) plus a rolling `latest` tag for convenience.
- Pushed to Azure Container Registry (ACR) via the `Docker@2` task and a pre-configured
  ACR service connection (`containerRegistry` variable).

## Task 3 — Kubernetes deployment

- Cluster: **Minikube** for local testing (`minikube start`), or **AKS** for cloud — the
  manifests are cluster-agnostic.
- `k8s/deployment.yaml` — 2 replicas, `RollingUpdate` strategy with `maxUnavailable: 0` so
  the app never drops below its desired capacity during a rollout.
- `k8s/service.yaml` — ClusterIP exposing port 80 → container port 8080.
- No Kubernetes service connection is used — Azure DevOps rejects service connections that
  resolve to a loopback address, which is what Minikube's API server listens on. Instead, the
  deploy stage runs `kubectl` directly on the self-hosted agent, using the kubeconfig the agent
  machine already trusts.
- The release stage in `azure-pipelines.yml`:
  1. Copies `k8s/*.yaml` into `$(Build.ArtifactStagingDirectory)` and substitutes the image tag
     **on that copy** — the tracked `k8s/deployment.yaml` in the repo is never modified in place
     (self-hosted agents don't clean the workspace between runs by default, so editing the
     tracked file directly would silently freeze in a stale tag after the first run).
  2. `kubectl apply -f` on the staged copies of `deployment.yaml`, `service.yaml`, `hpa.yaml`.
  3. Runs `kubectl rollout status` to **verify** the rollout actually succeeded before marking
     the pipeline green — a failed rollout fails the pipeline instead of silently deploying a
     crash-looping pod.
- Local verification: `kubectl get pods -n sample-app -w`, `kubectl port-forward svc/sample-app 8080:80 -n sample-app`.

## Task 4 — Monitoring: metrics & logging (implemented, not just described)

**Actually installed** on the local Minikube cluster via Helm — not just proposed on paper:

```bash
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update
kubectl create namespace monitoring
helm install monitoring prometheus-community/kube-prometheus-stack --namespace monitoring
```

This deploys, in the `monitoring` namespace:
- **Prometheus** — scrapes metrics from every namespace, including `sample-app`
- **Grafana** — pre-loaded with the standard `kube-prometheus-stack` dashboards (no manual
  dashboard-building required)
- **Alertmanager** — routes alerts (not wired to a real notification channel here, but the
  pipeline — Prometheus rule → Alertmanager → Slack/email/PagerDuty — is in place)
- **kube-state-metrics** and **node-exporter** — expose Kubernetes object state and node-level
  metrics respectively

**Verified working**: the `Kubernetes / Compute Resources / Namespace (Pods)` Grafana dashboard,
filtered to the `sample-app` namespace, shows live CPU request/limit values pulled directly from
the running pods (`0.100` request / `0.500` limit per pod — matching `k8s/deployment.yaml`
exactly), confirming Prometheus is actually scraping this workload, not just installed and idle.

**Accessing it locally:**
```bash
kubectl --namespace monitoring port-forward svc/monitoring-grafana 3000:80
# then browse to http://localhost:3000 (user: admin, password below)
kubectl get secret --namespace monitoring -l app.kubernetes.io/component=admin-secret \
  -o jsonpath="{.items[0].data.admin-password}" | base64 --decode
```

**What would be added for the app itself** (beyond the infra-level metrics above):
- `prometheus-net.AspNetCore` NuGet package exposing a `/metrics` endpoint with standard
  ASP.NET Core request metrics (duration, status code, in-flight requests).
- A `ServiceMonitor` resource pointing Prometheus at that `/metrics` endpoint, so
  application-level metrics join the same Grafana dashboards as the infra-level ones above.

**KPIs tracked (the "four golden signals"):**

| KPI | Why it matters | Example alert threshold |
|---|---|---|
| **Latency** (p50/p95/p99 request duration) | User-facing responsiveness | p95 > 500ms for 5 min |
| **Error rate** (5xx / total requests) | Correctness/availability | > 2% over 5 min |
| **Traffic** (requests/sec) | Capacity planning, anomaly detection | sudden drop-to-zero or spike |
| **CPU / memory saturation** (vs. requests/limits) | Feeds the HPA, early warning before throttling/OOMKill | CPU > 80% sustained 10 min |
| **Pod restarts / CrashLoopBackOff count** | Deployment or app stability | any restart in last 15 min |
| **Rollout status** | Deployment health | rollout stuck > 5 min |

**Logging:** structured JSON logs (`Serilog` + console sink) shipped via **Promtail** to
**Loki**, correlated with metrics in the same Grafana dashboards. Log-based alerts (e.g.
spike in `ERROR`-level log lines) complement the metric-based ones above. (Not installed
alongside Prometheus/Grafana here, to keep the local demo footprint reasonable — described
rather than implemented, per the assessment's "describe or implement" wording.)

## Bonus tasks

### Automated tests in the pipeline
`tests/SampleApp.Tests` (xUnit) covers the `WeatherController` (unit tests) and the
`/health/live`, `/health/ready`, and `/api/Weather` endpoints (integration tests, hosted
in-memory via `WebApplicationFactory<Program>` — no cluster needed to run them). The `Build`
stage's `dotnet test` step runs this project and is a real gate: a failing test fails the
build, and `PublishTestResults@2` surfaces results in the Azure DevOps Tests tab.

### Probes (see `k8s/deployment.yaml`)
- **Startup probe** absorbs slow boot time; liveness/readiness aren't evaluated until it
  passes once, so a slow-starting container isn't killed prematurely.
- **Liveness probe** is intentionally minimal (process-alive only) — it should never depend
  on external systems, or an unrelated outage (e.g. the database) would cause Kubernetes to
  restart perfectly healthy pods in a loop.
- **Readiness probe** does check dependencies. On failure the pod is removed from the
  Service's endpoints (no traffic) but is *not* restarted, so it can recover once the
  dependency returns — this is also what prevents a rolling update from routing traffic to a
  pod that's up but not actually ready to serve requests.

### Horizontal Pod Autoscaler (see `k8s/hpa.yaml`)
Scales 2→10 replicas on CPU >70% or memory >80% utilization (requires the `metrics-server`
add-on — `minikube addons enable metrics-server` locally, built-in on AKS). Scale-up is
immediate; scale-down waits 5 minutes of sustained low usage to avoid flapping.

### Architectural design — Multi-Region / Multi-Zone failover (conceptual)

```
                        ┌─────────────────────────┐
                        │  Azure Front Door /     │
                        │  Cloudflare (Global LB) │
                        │  health-probes each     │
                        │  region, routes by      │
                        │  latency + health       │
                        └───────────┬─────────────┘
                     ┌──────────────┴──────────────┐
                     ▼                             ▼
        ┌─────────────────────┐        ┌──────────────────────┐
        │  Region A (primary) │        │  Region B (standby/  │
        │  AKS - 3 zones      │        │  active) AKS-3 zones │
        │  sample-app pods    │        │  sample-app pods     │
        └──────────┬──────────┘        └───────────┬──────────┘
                   │                               │
                   ▼                               ▼
        ┌─────────────────────┐        ┌─────────────────────┐
        │  Primary DB         │◄──────►│ Read replica /      │
        │  (writes)           │  async │ geo-replicated      │
        │                     │  repl. │ (promoted on        │
        │                     │        │  failover)          │
        └─────────────────────┘        └─────────────────────┘
```

**Key components:**
- **Global load balancer** (Azure Front Door / Cloudflare / AWS Global Accelerator) does
  health-checked, latency-based routing across regions — if Region A's health probe fails,
  traffic shifts to Region B automatically, no DNS TTL wait.
- **Within each region:** AKS spread across 3 availability zones (`topologySpreadConstraints`
  on the Deployment) so a single zone outage doesn't take down the region.
- **Data replication:** primary region takes writes; secondary region has a geo-replicated
  read replica (e.g. Azure SQL geo-replication, or async replication for Postgres/Cosmos DB
  multi-region). On a regional failover, the replica is promoted to primary — this needs a
  documented RPO/RTO tradeoff, since async replication means some recent writes could be lost.
- **Stateless app tier:** the sample app itself holds no state, so scaling/failover of the
  compute layer is straightforward — all the complexity is in the data layer's replication and
  promotion strategy, which is where most real failover incidents actually go wrong.
