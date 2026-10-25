
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 25 | 25.0 | 1.303 | 1.493 | 2.00 | no |
| 50 | 50.0 | 1.387 | 1.526 | 2.96 | no |
| 75 | 75.0 | 1.437 | 1.544 | 4.04 | no |
| 100 | 100.0 | 1.466 | 1.571 | 5.01 | no |

No sustained rate — nothing to fit.

## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 50 | 50.0 | 0.137 | 0.212 | 2.00 | yes |
| 100 | 100.0 | 0.139 | 0.217 | 3.19 | yes |
| 150 | 150.0 | 0.146 | 0.227 | 4.67 | yes |

Sustained up to 150 /s (p99 < 1.0 s). Fit over 3 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.43 | 14.5 | 55% |
| Journal (Citus) | 0.00 | 4.4 | 17% |
| Read model (PG) | 0.00 | 2.8 | 11% |
| Kafka | 0.10 | 2.2 | 8% |
| Plugin / job workers | 0.07 | 1.2 | 4% |
| Worker silos | 0.03 | 0.6 | 2% |
| Membership + reminders (PG) | 0.01 | 0.6 | 2% |
| REST (Api role) | 0.00 | 0.2 | 1% |
| Redis | 0.01 | 0.0 | 0% |
| **total** | 0.65 | **26.3** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 7.8 | 3.3 |
| citus-coord | 18.4 | 0.0 |
| citus-w1 | 4.0 | 1.0 |
| citus-w2 | 4.0 | 1.0 |
| postgres-ops | 3.5 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.6 | 11 | 13 | 6 | 2 | 1 | 1 |
| 210 | 6.2 | 14 | 17 | 8 | 2 | 1 | 1 |
| 500 | 13.8 | 32 | 38 | 18 | 5 | 3 | 3 |
| 1,000 | 27.0 | 62 | 73 | 34 | 10 | 6 | 5 |
| 2,000 | 53.3 | 123 | 145 | 68 | 20 | 13 | 10 |
| 10,000 | 264.1 | 609 | 716 | 336 | 101 | 64 | 51 |
