
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 30 | 30.0 | 0.021 | 0.076 | 1.51 | yes |
| 34 | 34.0 | 0.022 | 0.081 | 1.70 | yes |
| 39 | 39.0 | 0.023 | 0.202 | 1.78 | yes |
| 45 | 45.0 | 0.024 | 0.115 | 2.19 | yes |
| 52 | 52.0 | 0.023 | 0.104 | 2.15 | yes |
| 60 | 59.9 | 0.028 | 0.305 | 2.63 | yes |
| 69 | 69.0 | 0.031 | 0.168 | 2.73 | yes |
| 79 | 79.1 | 0.036 | 0.190 | 2.81 | yes |
| 91 | 91.1 | 0.058 | 0.347 | 3.24 | yes |
| 105 | 105.1 | 0.081 | 0.335 | 3.45 | yes |
| 121 | 120.3 | 0.260 | 0.944 | 4.00 | yes |
| 139 | 107.6 | 0.261 | 1.610 | 3.98 | no |

Sustained up to 120 /s (p99 < 1.0 s). Fit over 11 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Journal (Citus) | 0.00 | 8.3 | 34% |
| Core silos | 0.37 | 6.5 | 27% |
| Projector | 0.11 | 3.9 | 16% |
| Worker silos | 0.19 | 2.4 | 10% |
| Read model (PG) | 0.00 | 2.3 | 9% |
| REST (Api role) | 0.02 | 1.4 | 6% |
| Plugin / job workers | 0.01 | 0.2 | 1% |
| Membership + reminders (PG) | 0.01 | -0.0 | -0% |
| Kafka | 0.25 | -0.6 | -3% |
| **total** | 0.97 | **24.3** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 3.2 | 5.6 |
| citus-coord | 13.4 | 0.0 |
| citus-w1 | 7.5 | 3.6 |
| citus-w2 | 7.5 | 3.5 |
| postgres-ops | 0.1 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.6 | 11 | 12 | 3 | 3 | 1 | 0 |
| 210 | 6.1 | 14 | 16 | 4 | 4 | 1 | 0 |
| 500 | 13.1 | 30 | 33 | 8 | 10 | 3 | -0 |
| 1,000 | 25.2 | 58 | 63 | 16 | 19 | 5 | -1 |
| 2,000 | 49.5 | 114 | 122 | 31 | 38 | 10 | -2 |
| 10,000 | 243.5 | 562 | 598 | 150 | 190 | 52 | -14 |
