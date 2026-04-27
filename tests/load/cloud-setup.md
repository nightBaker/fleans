# Cloud Load Test Setup — Phase 3

> **EXECUTION BLOCKED** until all Phase 1–2 prerequisites are complete:
> #237 (Docker Compose infra), #238 (BPMN fixtures), #239–#242 (k6 scripts), #243 (local baseline).

## Prerequisites

- Docker Compose **>= 2.17** on the test host VM (stable `--format json` Health field)
- Docker installed on the k6 runner VM
- k6 installed on the k6 runner VM: https://k6.io/docs/getting-started/installation/
- Both VMs in the **same VPC/subnet** (private IP connectivity, same availability zone)
- Pre-built `ghcr.io/nightbaker/fleans-api:load-test` image (see step 1 below)

> **Known limitation:** Domain events published by `WorkflowEventsPublisher` are scoped to the silo that published them. In multi-silo load tests, domain event counts in logs will be lower than total workflow count. This does not affect k6 throughput metrics or workflow execution correctness.

---

## Step 1 — Build and push the API image (run locally, once)

```bash
# From repo root
docker build \
  -t ghcr.io/nightbaker/fleans-api:load-test \
  -f src/Fleans/Fleans.Api/Dockerfile \
  src/Fleans/

docker push ghcr.io/nightbaker/fleans-api:load-test
```

Make the package public on ghcr.io (Settings → Package → Change visibility → Public), or
on the test host VM run `docker login ghcr.io` with a PAT that has `read:packages` scope.

---

## Step 2 — Provision VMs

Choose one provider. Use spot/preemptible to cut cost ~70%.

### AWS
```bash
# Test host (c5.9xlarge spot)
aws ec2 run-instances \
  --instance-type c5.9xlarge \
  --image-id ami-0abcdef1234567890 \
  --instance-market-options '{"MarketType":"spot","SpotOptions":{"MaxPrice":"0.50"}}' \
  --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=fleans-test-host}]' \
  --subnet-id <your-subnet-id>

# k6 runner (c5.4xlarge spot) — SAME subnet as test host
aws ec2 run-instances \
  --instance-type c5.4xlarge \
  --image-id ami-0abcdef1234567890 \
  --instance-market-options '{"MarketType":"spot","SpotOptions":{"MaxPrice":"0.25"}}' \
  --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=fleans-k6-runner}]' \
  --subnet-id <your-subnet-id>
```

### GCP
```bash
gcloud compute instances create fleans-test-host \
  --machine-type=c2-standard-30 \
  --provisioning-model=SPOT \
  --zone=us-central1-a \
  --image-family=ubuntu-2404-lts --image-project=ubuntu-os-cloud

gcloud compute instances create fleans-k6-runner \
  --machine-type=c2-standard-16 \
  --provisioning-model=SPOT \
  --zone=us-central1-a \
  --image-family=ubuntu-2404-lts --image-project=ubuntu-os-cloud
```

### Azure
```bash
az group create -n fleans-load-rg -l eastus

az vm create -g fleans-load-rg -n fleans-test-host \
  --size Standard_F32s_v2 --priority Spot --eviction-policy Deallocate \
  --image Ubuntu2404 --vnet-name fleans-vnet --subnet default

az vm create -g fleans-load-rg -n fleans-k6-runner \
  --size Standard_F16s_v2 --priority Spot --eviction-policy Deallocate \
  --image Ubuntu2404 --vnet-name fleans-vnet --subnet default
```

Note the **private IP** of the test host — you will use it as `K6_TARGET_URL`.

---

## Step 3 — Install dependencies on VMs

```bash
# On test host:
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-plugin curl python3
sudo usermod -aG docker ubuntu
newgrp docker

# Verify Docker Compose version >= 2.17
docker compose version

# On k6 runner:
sudo apt-get update
sudo gpg -k
sudo gpg --no-default-keyring \
  --keyring /usr/share/keyrings/k6-archive-keyring.gpg \
  --keyserver hkp://keyserver.ubuntu.com:80 \
  --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" \
  | sudo tee /etc/apt/sources.list.d/k6.list
sudo apt-get update && sudo apt-get install -y k6

# Set OS limits for high VU counts:
echo "* soft nofile 65535" | sudo tee -a /etc/security/limits.conf
echo "* hard nofile 65535" | sudo tee -a /etc/security/limits.conf
sudo sysctl -w net.ipv4.ip_local_port_range="1024 65535"
```

---

## Step 4 — Copy files to test host

```bash
# From repo root — copy tests/load/ and src/Fleans/ (build context for any local rebuilds)
rsync -avz tests/load/   ubuntu@<TEST_HOST_PUBLIC_IP>:~/fleans/tests/load/
# (image is pulled from ghcr.io, so src/ is optional)
```

---

## Step 5 — Run Phase 3A: Baseline confirmation (2 silos)

```bash
# On test host
cd ~/fleans/tests/load

docker compose up --scale api=2 -d
EXPECTED_REPLICA_COUNT=2 bash verify-cluster.sh

# On k6 runner
export K6_TARGET_URL=http://<TEST_HOST_PRIVATE_IP>:80

k6 run --out csv=results/cloud/2silo-linear.csv   scripts/linear.js
k6 run --out csv=results/cloud/2silo-parallel.csv scripts/parallel.js
k6 run --out csv=results/cloud/2silo-events.csv   scripts/events.js
k6 run --out csv=results/cloud/2silo-mixed.csv    scripts/mixed.js
```

---

## Step 6 — Run Phase 3B: Saturation search (2 silos)

```bash
# On k6 runner
k6 run --out csv=results/cloud/2silo-saturation.csv scripts/linear-saturation.js
```

Record: the first VU stage where **error rate > 1%** or **p95 > 2 s**. This is the 2-silo saturation point.

---

## Step 7 — Run Phase 3C: Horizontal scaling (4 silos)

```bash
# On test host
docker compose up --scale api=4 --no-recreate -d

# REQUIRED: nginx resolves 'server api:5000' only at startup or reload.
# Without this reload, the 2 new silos receive zero HTTP traffic and Phase 3C results are invalid.
docker compose exec nginx nginx -s reload

EXPECTED_REPLICA_COUNT=4 bash verify-cluster.sh

# On k6 runner
k6 run --out csv=results/cloud/4silo-saturation.csv scripts/linear-saturation.js
k6 run --out csv=results/cloud/4silo-mixed.csv      scripts/mixed.js
```

Expected: near-linear throughput improvement (1.8–2× compared to Phase 3B saturation point).

---

## Step 8 — Run Phase 3D: Mixed workload at scale

```bash
# On k6 runner — run at 2× the 2-silo saturation VU count with 4 silos
# Requires mixed.js to support __ENV.SCALE_FACTOR (constraint on #242)
k6 run --env SCALE_FACTOR=2 \
       --out csv=results/cloud/4silo-mixed-scaled.csv \
       scripts/mixed.js
```

---

## Step 9 — Collect results and teardown

```bash
# On test host — save final logs before stopping
bash cloud-teardown.sh
```

Then destroy the VMs (see `cloud-teardown.sh`).

---

## Results

CSV files are written to `results/cloud/` on the k6 runner. Copy them back to `tests/load/results/cloud/` in the repo for the final `report.md`.

Grafana dashboards are accessible at `http://<TEST_HOST_PUBLIC_IP>:3000` (admin / admin) during the test run.

Populate `tests/load/results/cloud/report.md` with the comparison table:

| Scenario | VUs | Host | RPS | p50 (ms) | p95 (ms) | Error % | Saturated? |
|----------|-----|------|-----|----------|----------|---------|------------|
| linear   | 100 | local-mac (2 silos)   | … | … | … | … | No |
| linear   | 100 | cloud-linux (2 silos) | … | … | … | … | No |

> **Known limitation:** Domain event counts in logs will be lower than total workflow count in multi-silo runs due to memory stream silo-scoping. This does not affect throughput metrics.

---

## Troubleshooting

**Containers not becoming healthy:** Check `docker compose logs api`. Common causes: Redis connection misconfiguration, missing env vars, or port conflicts.

**Stage 3 fails (not all replicas receiving traffic):** nginx was not reloaded after scaling. Run `docker compose exec nginx nginx -s reload` and re-run `verify-cluster.sh`.

**k6 "dial: too many open files":** Set `ulimit -n 65535` on the k6 runner VM before running k6.

**Orleans silos not clustering:** Verify `ConnectionStrings__orleans-redis: redis:6379` and `FLEANS_LOAD_TEST_MODE: "true"` are set in `docker-compose.yml`. Check `docker compose logs api | grep -i cluster`.
