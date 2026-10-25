
## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 200 | 200.3 | 0.146 | 0.312 | 4.84 | yes |
| 250 | 128.4 | 0.148 | 0.587 | 4.81 | no |
| 300 | 0.0 | nan | nan | 6.42 | no |

Sustained up to 200 /s (p99 < 1.0 s). Fit over 1 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.00 | 9.3 | 38% |
| Journal (Citus) | 0.00 | 3.6 | 15% |
| Projector | 0.00 | 3.4 | 14% |
| Read model (PG) | 0.00 | 2.1 | 9% |
| REST (Api role) | 0.00 | 1.7 | 7% |
| Plugin / job workers | 0.00 | 1.7 | 7% |
| Kafka | 0.00 | 1.6 | 7% |
| Worker silos | 0.00 | 0.8 | 3% |
| Membership + reminders (PG) | 0.00 | 0.0 | 0% |
| Redis | 0.00 | 0.0 | 0% |
| **total** | 0.00 | **24.2** | |

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
| 150 | 3.6 | 8 | 10 | 3 | 1 | 1 | 1 |
| 210 | 5.1 | 12 | 13 | 4 | 2 | 1 | 1 |
| 500 | 12.1 | 28 | 32 | 11 | 4 | 2 | 2 |
| 1,000 | 24.2 | 56 | 64 | 21 | 8 | 5 | 4 |
| 2,000 | 48.4 | 112 | 127 | 43 | 17 | 10 | 7 |
| 10,000 | 241.8 | 558 | 637 | 214 | 83 | 49 | 36 |
