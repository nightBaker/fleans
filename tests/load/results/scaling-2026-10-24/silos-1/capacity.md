
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 10 | 10.0 | 0.024 | 0.256 | 0.70 | yes |
| 11 | 11.0 | 0.023 | 0.074 | 0.88 | yes |
| 13 | 13.0 | 0.023 | 0.248 | 0.89 | yes |
| 15 | 15.0 | 0.022 | 0.127 | 0.81 | yes |
| 17 | 17.0 | 0.021 | 0.079 | 0.95 | yes |
| 20 | 20.0 | 0.022 | 0.613 | 1.00 | yes |
| 23 | 23.0 | 0.021 | 0.205 | 1.33 | yes |
| 26 | 26.0 | 0.027 | 0.245 | 1.35 | yes |
| 30 | 30.0 | 0.092 | 0.476 | 1.21 | yes |
| 35 | 35.1 | 0.163 | 0.765 | 1.47 | yes |
| 40 | 39.9 | 0.317 | 1.120 | 1.51 | no |
| 46 | 45.9 | 0.815 | 1.494 | 1.39 | no |
| 53 | 45.6 | 1.149 | 2.394 | 1.56 | no |

Sustained up to 35 /s (p99 < 1.0 s). Fit over 10 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Journal (Citus) | 0.00 | 8.5 | 30% |
| Core silos | 0.15 | 5.6 | 20% |
| Projector | 0.07 | 3.9 | 14% |
| Kafka | 0.14 | 3.8 | 13% |
| Worker silos | 0.07 | 2.7 | 9% |
| Read model (PG) | 0.02 | 2.1 | 7% |
| REST (Api role) | 0.02 | 1.3 | 5% |
| Plugin / job workers | 0.01 | 0.8 | 3% |
| Membership + reminders (PG) | 0.01 | -0.0 | -0% |
| **total** | 0.48 | **28.7** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 5.1 | 5.8 |
| citus-coord | 13.5 | 0.0 |
| citus-w1 | 7.5 | 4.2 |
| citus-w2 | 7.6 | 4.2 |
| postgres-ops | 0.2 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.8 | 11 | 14 | 2 | 3 | 1 | 2 |
| 210 | 6.5 | 15 | 20 | 3 | 4 | 1 | 2 |
| 500 | 14.8 | 34 | 44 | 7 | 10 | 2 | 5 |
| 1,000 | 29.2 | 67 | 87 | 13 | 20 | 5 | 9 |
| 2,000 | 57.9 | 134 | 172 | 26 | 39 | 10 | 18 |
| 10,000 | 287.4 | 663 | 855 | 130 | 196 | 48 | 88 |
