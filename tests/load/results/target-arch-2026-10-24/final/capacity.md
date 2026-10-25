
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 50 | 50.0 | 0.024 | 0.334 | 2.73 | yes |
| 100 | 100.0 | 0.031 | 0.255 | 5.40 | yes |
| 150 | 146.1 | 22.289 | 39.353 | 8.72 | no |
| 200 | 68.7 | 108.946 | 118.777 | 9.20 | no |

Sustained up to 100 /s (p99 < 1.0 s). Fit over 2 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.10 | 20.6 | 42% |
| Journal (Citus) | 0.00 | 9.0 | 18% |
| Projector | 0.11 | 5.2 | 11% |
| Worker silos | 0.16 | 4.0 | 8% |
| Kafka | 0.00 | 3.4 | 7% |
| Read model (PG) | 0.00 | 2.9 | 6% |
| REST (Api role) | 0.00 | 2.0 | 4% |
| Membership + reminders (PG) | 0.01 | 1.0 | 2% |
| Plugin / job workers | 0.02 | 0.7 | 1% |
| Redis | 0.00 | 0.1 | 0% |
| **total** | 0.39 | **49.0** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 5.0 |
| citus-coord | 0.0 | 0.0 |
| citus-w1 | 7.5 | 3.2 |
| citus-w2 | 7.4 | 3.1 |
| postgres-ops | 4.3 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 7.7 | 18 | 21 | 7 | 3 | 1 | 1 |
| 210 | 10.7 | 25 | 28 | 10 | 4 | 1 | 2 |
| 500 | 24.9 | 57 | 66 | 24 | 10 | 3 | 4 |
| 1,000 | 49.4 | 114 | 132 | 48 | 21 | 7 | 8 |
| 2,000 | 98.4 | 227 | 263 | 95 | 42 | 13 | 16 |
| 10,000 | 490.3 | 1132 | 1311 | 477 | 208 | 67 | 78 |

## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 50 | 50.0 | 0.137 | 0.212 | 1.86 | yes |
| 100 | 100.0 | 0.138 | 0.213 | 3.12 | yes |
| 150 | 150.0 | 0.139 | 0.216 | 4.32 | yes |

Sustained up to 150 /s (p99 < 1.0 s). Fit over 3 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.20 | 9.4 | 39% |
| Journal (Citus) | 0.00 | 3.5 | 14% |
| Projector | 0.13 | 2.9 | 12% |
| Kafka | 0.17 | 2.4 | 10% |
| Read model (PG) | 0.01 | 1.9 | 8% |
| REST (Api role) | 0.02 | 1.5 | 6% |
| Plugin / job workers | 0.07 | 1.1 | 4% |
| Worker silos | 0.05 | 1.1 | 4% |
| Membership + reminders (PG) | 0.00 | 0.7 | 3% |
| Redis | 0.01 | 0.0 | 0% |
| **total** | 0.65 | **24.5** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 2.6 |
| citus-coord | 4.9 | 0.0 |
| citus-w1 | 3.0 | 0.9 |
| citus-w2 | 3.0 | 0.9 |
| postgres-ops | 3.5 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.3 | 10 | 12 | 4 | 1 | 1 | 1 |
| 210 | 5.8 | 13 | 16 | 5 | 2 | 1 | 2 |
| 500 | 12.9 | 30 | 36 | 11 | 4 | 2 | 3 |
| 1,000 | 25.1 | 58 | 69 | 22 | 8 | 4 | 6 |
| 2,000 | 49.6 | 115 | 136 | 44 | 16 | 9 | 11 |
| 10,000 | 245.6 | 567 | 674 | 219 | 81 | 44 | 55 |
