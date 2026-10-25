
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 100 | 100.0 | 0.025 | 0.130 | 4.68 | yes |
| 150 | 150.0 | 0.031 | 0.194 | 6.14 | yes |
| 200 | 198.9 | 0.135 | 1.011 | 7.27 | no |

Sustained up to 150 /s (p99 < 1.0 s). Fit over 2 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.92 | 11.2 | 39% |
| Journal (Citus) | 0.01 | 8.1 | 28% |
| Kafka | 0.01 | 3.0 | 10% |
| Projector | 0.26 | 2.3 | 8% |
| Read model (PG) | 0.01 | 1.9 | 7% |
| REST (Api role) | 0.06 | 1.4 | 5% |
| Worker silos | 0.47 | 0.5 | 2% |
| Plugin / job workers | 0.06 | 0.4 | 1% |
| Membership + reminders (PG) | 0.00 | 0.0 | 0% |
| **total** | 1.80 | **28.9** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 2.4 | 4.8 |
| citus-coord | 0.0 | 0.0 |
| citus-w1 | 7.5 | 3.1 |
| citus-w2 | 7.5 | 3.1 |
| postgres-ops | 0.0 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 6.1 | 14 | 17 | 6 | 3 | 1 | 1 |
| 210 | 7.9 | 18 | 22 | 8 | 4 | 1 | 1 |
| 500 | 16.2 | 37 | 45 | 15 | 9 | 2 | 3 |
| 1,000 | 30.7 | 71 | 87 | 28 | 19 | 5 | 7 |
| 2,000 | 59.5 | 137 | 169 | 54 | 37 | 9 | 14 |
| 10,000 | 290.5 | 670 | 830 | 261 | 186 | 45 | 69 |

## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 200 | 200.0 | 0.141 | 0.233 | 4.52 | yes |
| 250 | 250.0 | 0.145 | 0.239 | 5.34 | yes |
| 300 | 299.3 | 0.164 | 0.410 | 6.30 | yes |
| 350 | 339.7 | 0.215 | 0.839 | 7.19 | yes |
| 400 | 341.8 | 0.388 | 1.229 | 7.93 | no |

Sustained up to 340 /s (p99 < 1.0 s). Fit over 4 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.93 | 4.4 | 27% |
| Journal (Citus) | 0.00 | 3.9 | 24% |
| Projector | 0.18 | 2.1 | 13% |
| Kafka | 0.00 | 1.8 | 11% |
| Read model (PG) | 0.00 | 1.2 | 8% |
| REST (Api role) | 0.13 | 1.1 | 7% |
| Plugin / job workers | 0.18 | 0.9 | 6% |
| Worker silos | 0.06 | 0.4 | 3% |
| Membership + reminders (PG) | 0.00 | 0.1 | 0% |
| **total** | 1.47 | **16.0** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 1.6 | 2.3 |
| citus-coord | 3.8 | 0.0 |
| citus-w1 | 3.0 | 0.8 |
| citus-w2 | 3.0 | 0.8 |
| postgres-ops | 0.0 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 3.9 | 9 | 10 | 4 | 1 | 0 | 1 |
| 210 | 4.8 | 11 | 13 | 4 | 2 | 1 | 1 |
| 500 | 9.5 | 22 | 26 | 7 | 5 | 1 | 2 |
| 1,000 | 17.5 | 40 | 49 | 12 | 9 | 3 | 4 |
| 2,000 | 33.5 | 77 | 95 | 22 | 18 | 6 | 8 |
| 10,000 | 161.8 | 373 | 464 | 103 | 90 | 28 | 42 |
