
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 30 | 30.0 | 0.021 | 0.037 | 1.61 | yes |
| 34 | 34.0 | 0.021 | 0.042 | 1.86 | yes |
| 39 | 39.0 | 0.021 | 0.047 | 2.03 | yes |
| 45 | 45.0 | 0.021 | 0.046 | 2.17 | yes |
| 52 | 52.0 | 0.022 | 0.049 | 2.64 | yes |
| 60 | 60.0 | 0.022 | 0.106 | 3.02 | yes |
| 69 | 69.0 | 0.022 | 0.065 | 3.16 | yes |
| 79 | 79.0 | 0.023 | 0.405 | 3.45 | yes |
| 91 | 90.9 | 0.025 | 1.204 | 4.07 | no |

Sustained up to 79 /s (p99 < 1.0 s). Fit over 8 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.04 | 18.9 | 51% |
| Journal (Citus) | 0.00 | 7.1 | 19% |
| Worker silos | 0.04 | 5.2 | 14% |
| Projector | 0.09 | 4.1 | 11% |
| Read model (PG) | 0.03 | 1.7 | 5% |
| REST (Api role) | 0.01 | 1.7 | 5% |
| Plugin / job workers | 0.02 | 1.3 | 3% |
| Membership + reminders (PG) | 0.01 | 0.0 | 0% |
| Kafka | 0.35 | -2.7 | -7% |
| **total** | 0.58 | **37.5** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 3.7 | 5.4 |
| citus-coord | 8.4 | 0.0 |
| citus-w1 | 14.9 | 7.0 |
| citus-w2 | 0.0 | 0.0 |
| postgres-ops | 0.1 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 6.2 | 14 | 15 | 7 | 2 | 1 | -0 |
| 210 | 8.4 | 19 | 20 | 9 | 3 | 1 | -0 |
| 500 | 19.3 | 45 | 44 | 22 | 8 | 2 | -2 |
| 1,000 | 38.0 | 88 | 85 | 44 | 16 | 4 | -5 |
| 2,000 | 75.5 | 174 | 167 | 87 | 33 | 8 | -12 |
| 10,000 | 375.2 | 866 | 823 | 437 | 164 | 40 | -61 |
