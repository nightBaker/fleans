# Zeebe reference points

What "Zeebe's numbers" means in the capacity model of [`report.md`](report.md). Every figure is
Camunda's own, from the cited post; the hardware and process differ per benchmark, so each row is
used only for the comparison it supports.

| # | Source | Process | Cluster | Throughput | Latency |
|---|---|---|---|---:|---|
| Z1 | [Zeebe batch processing (2023-03)](https://camunda.com/blog/2023/03/zeebe-batch-processing/) | "based on user requirements" — several service tasks | 8 brokers × (8 vCPU, 16 GB, SSD/XFS; GKE n2-standard-8 each), 24 partitions, replication factor 4 | target **~500 PI/s** (reached by combining the hack-day POCs) | **p99 < 1 s** |
| Z2 | same post, weekly benchmark | 1 service task, big payload | 3 brokers, 3 partitions, RF 3 | 133 → **163 PI/s** (avg) after the POCs | ~2× lower than before |
| Z3 | [Broker scaling and performance (2023-12)](https://camunda.github.io/zeebe-chaos/2023/12/20/Broker-scaling-performance/) | 1 service task, 32 KiB payload, 50 ms worker | 3 brokers × (**1.35 CPU**, 4 GiB, 32 GiB SSD), 6 partitions, RF 3 | 150 PI/s steady; **~210 PI/s** max ("relatively high backpressure") | — |
| Z4 | same post | 10 tasks + 2 one-minute timers | 3 → 6 brokers × 1.35 CPU | 40 → **65 PI/s** (650 jobs/s) | — |
| Z5 | [Scaling Zeebe horizontally (2019-08)](https://camunda.com/blog/2019/08/zeebe-horizontal-scalability/) | start → 1 service task → end | brokers × (7 CPU, 16 GB, 500 GB SSD) | 1 broker 1 300, 3 → 3 600, 6 → 5 200, 12 → 8 200, 24 → **14 000 PI/s** | — |
| Z6 | [Zeebe performance 2023 in review](https://camunda.com/blog/2024/02/zeebe-performance-2023-year-in-review/) | 1 task, big payload, 50 ms worker | 3 brokers, 2 gateways, 3 partitions, RF 3 | 150 PI/s (the standard load) | job push: p50 −50 %, p99 −75 % |

Notes:

- **Z1 is the headline**: 500 PI/s at p99 < 1 s on 64 broker vCPU (gateways, workers and the
  exporters / Elasticsearch / Operate not included). The Fleans roadmap cites the same post.
- **Z3/Z6 is the efficiency point**: ~4 broker CPUs carry 150–210 PI/s of a one-task process with
  three-way replication — about **50 PI/s per broker CPU** at the ceiling, with RF 3 included.
- Z5 predates the 2020+ stabilisation work and Camunda itself marks the numbers as historical.
- Zeebe's unit of work is a *job* (create → activate → complete); a Fleans script task runs inside
  the engine, so `linear-10` (10 script tasks) is not 10 Zeebe jobs. The comparable Fleans process
  for Z2/Z3/Z6 is `load-one-task` (one external `e2e-probe` task completing after 50 ms).
