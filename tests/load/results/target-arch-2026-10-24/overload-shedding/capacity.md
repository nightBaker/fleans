
## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 200 | 200.0 | 0.142 | 0.225 | 4.74 | yes |
| 250 | 129.0 | 0.152 | 0.553 | 4.83 | no |
| 300 | 0.0 | nan | nan | 5.18 | no |

Sustained up to 200 /s (p99 < 1.0 s). Fit over 1 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.00 | 9.1 | 39% |
| Projector | 0.00 | 3.7 | 16% |
| Journal (Citus) | 0.00 | 3.4 | 14% |
| Read model (PG) | 0.00 | 2.0 | 8% |
| Plugin / job workers | 0.00 | 1.8 | 7% |
| REST (Api role) | 0.00 | 1.7 | 7% |
| Kafka | 0.00 | 1.6 | 7% |
| Worker silos | 0.00 | 0.3 | 1% |
| Membership + reminders (PG) | 0.00 | 0.0 | 0% |
| Redis | 0.00 | 0.0 | 0% |
| **total** | 0.00 | **23.7** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 2.3 |
| citus-coord | 0.0 | 0.0 |
| citus-w1 | 3.0 | 0.8 |
| citus-w2 | 3.0 | 0.8 |
| postgres-ops | 0.0 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 3.6 | 8 | 9 | 3 | 1 | 1 | 1 |
| 210 | 5.0 | 11 | 13 | 4 | 2 | 1 | 1 |
| 500 | 11.9 | 27 | 31 | 11 | 4 | 2 | 2 |
| 1,000 | 23.7 | 55 | 63 | 21 | 8 | 5 | 4 |
| 2,000 | 47.4 | 109 | 125 | 42 | 16 | 9 | 8 |
| 10,000 | 237.1 | 547 | 627 | 211 | 78 | 46 | 38 |
