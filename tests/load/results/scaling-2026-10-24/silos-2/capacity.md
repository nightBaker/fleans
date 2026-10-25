
## load-linear-10

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 20 | 20.0 | 0.022 | 0.100 | 1.28 | yes |
| 23 | 23.0 | 0.022 | 0.125 | 1.15 | yes |
| 26 | 26.0 | 0.023 | 0.209 | 1.63 | yes |
| 30 | 30.0 | 0.023 | 0.129 | 1.38 | yes |
| 34 | 34.0 | 0.022 | 0.105 | 1.88 | yes |
| 40 | 40.0 | 0.027 | 0.306 | 1.78 | yes |
| 46 | 46.0 | 0.032 | 0.242 | 2.11 | yes |
| 53 | 52.9 | 0.060 | 0.320 | 2.05 | yes |
| 61 | 60.7 | 0.104 | 0.998 | 2.23 | yes |
| 70 | 70.1 | 0.113 | 0.599 | 2.52 | yes |
| 80 | 80.0 | 0.196 | 0.858 | 2.52 | yes |
| 93 | 84.4 | 0.345 | 1.606 | 2.91 | no |

Sustained up to 80 /s (p99 < 1.0 s). Fit over 11 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Journal (Citus) | 0.00 | 8.5 | 39% |
| Core silos | 0.28 | 5.4 | 25% |
| Projector | 0.10 | 3.8 | 18% |
| Worker silos | 0.12 | 2.3 | 10% |
| Read model (PG) | 0.02 | 2.2 | 10% |
| REST (Api role) | 0.02 | 1.3 | 6% |
| Plugin / job workers | 0.04 | 0.8 | 4% |
| Membership + reminders (PG) | 0.01 | 0.0 | 0% |
| Kafka | 0.32 | -2.5 | -12% |
| **total** | 0.91 | **21.7** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 3.9 | 5.6 |
| citus-coord | 13.2 | 0.0 |
| citus-w1 | 7.5 | 3.7 |
| citus-w2 | 7.6 | 3.8 |
| postgres-ops | 0.1 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.2 | 10 | 10 | 3 | 3 | 1 | -0 |
| 210 | 5.5 | 13 | 13 | 3 | 4 | 1 | -0 |
| 500 | 11.8 | 27 | 27 | 7 | 10 | 3 | -2 |
| 1,000 | 22.7 | 52 | 51 | 13 | 20 | 5 | -5 |
| 2,000 | 44.4 | 102 | 98 | 26 | 39 | 10 | -11 |
| 10,000 | 218.4 | 504 | 477 | 126 | 195 | 51 | -57 |
