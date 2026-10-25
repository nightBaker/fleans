
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 50 | 50.0 | 0.023 | 0.068 | 2.69 | yes |
| 100 | 100.0 | 0.025 | 0.073 | 4.95 | yes |
| 150 | 149.8 | 0.038 | 0.404 | 6.50 | yes |

Sustained up to 150 /s (p99 < 1.0 s). Fit over 3 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.32 | 15.5 | 42% |
| Journal (Citus) | 0.00 | 8.6 | 23% |
| Projector | 0.15 | 4.0 | 11% |
| Read model (PG) | 0.00 | 2.9 | 8% |
| Worker silos | 0.29 | 2.3 | 6% |
| REST (Api role) | 0.00 | 1.9 | 5% |
| Kafka | 0.28 | 1.2 | 3% |
| Plugin / job workers | 0.01 | 0.1 | 0% |
| Membership + reminders (PG) | 0.00 | 0.1 | 0% |
| Redis | 0.00 | 0.0 | 0% |
| **total** | 1.06 | **36.6** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 5.5 |
| citus-coord | 9.1 | 0.0 |
| citus-w1 | 7.4 | 3.3 |
| citus-w2 | 7.5 | 3.4 |
| postgres-ops | 0.1 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 6.5 | 15 | 18 | 6 | 3 | 1 | 1 |
| 210 | 8.7 | 20 | 23 | 8 | 4 | 1 | 1 |
| 500 | 19.3 | 45 | 51 | 19 | 10 | 3 | 2 |
| 1,000 | 37.6 | 87 | 98 | 36 | 20 | 7 | 3 |
| 2,000 | 74.2 | 171 | 192 | 72 | 40 | 13 | 6 |
| 10,000 | 366.6 | 846 | 946 | 358 | 198 | 67 | 27 |

## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 100 | 100.0 | 0.139 | 0.216 | 2.81 | yes |
| 150 | 150.0 | 0.140 | 0.224 | 4.18 | yes |
| 200 | 199.9 | 0.145 | 0.264 | 5.09 | yes |
| 250 | 0.0 | nan | nan | 4.62 | no |

Sustained up to 200 /s (p99 < 1.0 s). Fit over 3 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.27 | 8.2 | 37% |
| Journal (Citus) | 0.00 | 3.7 | 17% |
| Projector | 0.17 | 2.7 | 12% |
| Kafka | 0.09 | 2.0 | 9% |
| Read model (PG) | 0.01 | 2.0 | 9% |
| REST (Api role) | 0.02 | 1.6 | 7% |
| Plugin / job workers | 0.07 | 1.5 | 7% |
| Worker silos | 0.06 | 0.5 | 2% |
| Membership + reminders (PG) | 0.00 | 0.1 | 0% |
| Redis | 0.01 | 0.0 | 0% |
| **total** | 0.69 | **22.2** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 2.7 |
| citus-coord | 5.6 | 0.0 |
| citus-w1 | 3.0 | 1.0 |
| citus-w2 | 3.0 | 1.0 |
| postgres-ops | 0.0 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.0 | 9 | 11 | 3 | 1 | 1 | 1 |
| 210 | 5.4 | 12 | 15 | 5 | 2 | 1 | 1 |
| 500 | 11.8 | 27 | 32 | 10 | 4 | 2 | 3 |
| 1,000 | 22.9 | 53 | 63 | 20 | 9 | 5 | 5 |
| 2,000 | 45.2 | 104 | 124 | 39 | 17 | 9 | 10 |
| 10,000 | 223.2 | 515 | 611 | 190 | 85 | 46 | 47 |
