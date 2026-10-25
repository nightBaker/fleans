
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 60 | 60.0 | 0.022 | 0.048 | 2.77 | yes |
| 69 | 69.0 | 0.023 | 0.053 | 3.43 | yes |
| 79 | 79.0 | 0.024 | 0.941 | 3.75 | yes |
| 91 | 91.0 | 0.024 | 0.079 | 4.11 | yes |
| 104 | 104.0 | 0.025 | 0.167 | 4.96 | yes |
| 120 | 117.7 | 0.031 | 0.836 | 5.60 | yes |
| 138 | 138.0 | 0.072 | 0.899 | 6.23 | yes |
| 159 | 143.1 | 1.210 | 4.576 | 6.71 | no |

Sustained up to 138 /s (p99 < 1.0 s). Fit over 7 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.34 | 16.2 | 43% |
| Journal (Citus) | 0.00 | 8.9 | 23% |
| Projector | 0.13 | 3.5 | 9% |
| Kafka | 0.00 | 3.1 | 8% |
| Read model (PG) | 0.00 | 2.2 | 6% |
| REST (Api role) | 0.00 | 2.0 | 5% |
| Worker silos | 0.36 | 1.7 | 4% |
| Plugin / job workers | 0.01 | 0.3 | 1% |
| Membership + reminders (PG) | 0.00 | 0.1 | 0% |
| **total** | 0.83 | **38.0** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 2.7 | 5.3 |
| citus-coord | 13.2 | 0.0 |
| citus-w1 | 7.5 | 3.6 |
| citus-w2 | 7.4 | 3.5 |
| postgres-ops | 0.1 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 6.5 | 15 | 18 | 6 | 3 | 1 | 1 |
| 210 | 8.8 | 20 | 24 | 9 | 4 | 1 | 2 |
| 500 | 19.8 | 46 | 54 | 19 | 10 | 3 | 4 |
| 1,000 | 38.8 | 90 | 106 | 38 | 20 | 5 | 7 |
| 2,000 | 76.8 | 177 | 211 | 75 | 41 | 10 | 14 |
| 10,000 | 380.6 | 878 | 1047 | 374 | 205 | 52 | 72 |
