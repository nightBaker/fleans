
## load-one-task

| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |
|---:|---:|---:|---:|---:|:-:|
| 100 | 100.0 | 0.136 | 0.212 | 3.12 | yes |

Sustained up to 100 /s (p99 < 1.0 s). Fit over 1 rate(s):

| tier | idle cores | ms CPU per instance | share |
|---|---:|---:|---:|
| Core silos | 0.00 | 9.8 | 31% |
| Kafka | 0.00 | 4.8 | 15% |
| Projector | 0.00 | 4.6 | 15% |
| Journal (Citus) | 0.00 | 3.8 | 12% |
| Plugin / job workers | 0.00 | 2.3 | 8% |
| Read model (PG) | 0.00 | 2.1 | 7% |
| Worker silos | 0.00 | 1.9 | 6% |
| REST (Api role) | 0.00 | 1.7 | 6% |
| Membership + reminders (PG) | 0.00 | 0.1 | 0% |
| Redis | 0.00 | 0.1 | 0% |
| **total** | 0.00 | **31.2** | |

| database server | transactions / instance | WAL KB / instance |
|---|---:|---:|
| postgres | 4.0 | 2.3 |
| citus-coord | 0.0 | 0.0 |
| citus-w1 | 3.0 | 0.9 |
| citus-w2 | 2.9 | 0.8 |
| postgres-ops | 0.0 | 0.0 |

Projection (k = 1.5 vCPU per laptop core, sized for 65% CPU):

| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 150 | 4.7 | 11 | 14 | 3 | 1 | 1 | 2 |
| 210 | 6.6 | 15 | 19 | 5 | 2 | 1 | 2 |
| 500 | 15.6 | 36 | 46 | 11 | 4 | 2 | 6 |
| 1,000 | 31.2 | 72 | 91 | 23 | 9 | 5 | 11 |
| 2,000 | 62.4 | 144 | 183 | 45 | 18 | 10 | 22 |
| 10,000 | 312.0 | 720 | 914 | 226 | 88 | 48 | 111 |
