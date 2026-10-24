#!/usr/bin/env python3
"""capacity.py — a resource model from one throughput-ceiling.sh results directory.

    python3 tests/load/scripts/capacity.py tests/load/results/<run> [--k 1.5] [--util 0.65]

For each process it takes the sustained rates (completed/s within 3 % of the offered rate, nothing
left running, p99 under --p99 seconds), fits CPU = a + b × rate per service over them (cores, from
<process>-<rate>.cpu.txt), and projects the cores needed for the target rates:

    vCPU(T) = (Σ a + T × Σ b) × k / util

  k     cloud vCPU per laptop core (one M1 Pro performance core ≈ 1.5 vCPU of a current x86 cloud
        instance, where one vCPU is a hyperthread) — an assumption, see report.md;
  util  the CPU utilisation a cluster is sized for (headroom for bursts and failover).

"with HA" adds what the laptop stack does not run: a streaming standby per journal node (WAL
replay, ~0.3 of the primary's CPU, --standby) and Kafka replication factor 3 (--kafka-rf: each
record written rf times, ~0.75 of the leader's CPU per extra copy).

The per-instance cost b (ms of laptop CPU per completed instance) is the number to compare across
builds; the projection is linear by construction, so it only holds while the design scales out —
which is what the target architecture removes the shared points for. Database transactions and WAL
per instance (<process>-<rate>.db.txt) are averaged over the sustained rates.
"""
import argparse
import glob
import os
import re
import statistics
import sys

TARGETS = [150, 210, 500, 1000, 2000, 10000]

GROUPS = {   # service → tier, for the summary
    'fleans-api': 'REST (Api role)', 'nginx': 'REST (Api role)',
    'fleans-core': 'Core silos', 'fleans-worker': 'Worker silos', 'fleans-plugin': 'Plugin / job workers',
    'fleans-projector': 'Projector',
    'citus-coord': 'Journal (Citus)', 'citus-w1': 'Journal (Citus)', 'citus-w2': 'Journal (Citus)',
    'postgres': 'Read model (PG)', 'postgres-replica': 'Read model (PG)',
    'postgres-ops': 'Membership + reminders (PG)', 'kafka': 'Kafka', 'redis': 'Redis',
}


def parse_summary_row(path):
    # throughput-window.sql output: | started | completed | failed | not_finished | completed_per_s | p50 | p95 | p99 | max
    for line in open(path):
        cells = [c.strip() for c in line.split('|')]
        if len(cells) == 9 and cells[0].isdigit():
            vals = [float(c) if c else float('nan') for c in cells]
            return dict(zip(['started', 'completed', 'failed', 'not_finished', 'per_s', 'p50', 'p95', 'p99', 'max'], vals))
    return None


def parse_cpu(path):
    out = {}
    for line in open(path):
        m = re.match(r'(\S+)\s+([\d.]+) %', line)
        if m and m.group(1) != 'total' and not m.group(1).startswith('orleans-redis'):
            out[m.group(1)] = float(m.group(2)) / 100.0
    return out


def parse_db(path):
    out = {}
    if not os.path.exists(path):
        return out
    for line in open(path):
        parts = line.split()
        if len(parts) == 3 and parts[0] != 'server':
            out[parts[0]] = (float(parts[1]), float(parts[2]))
    return out


def fit(xs, ys):
    if len(xs) == 1:
        return 0.0, ys[0] / xs[0]
    mx, my = statistics.mean(xs), statistics.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    b = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    a = my - b * mx
    if a < 0:                       # noise: force through the origin
        return 0.0, sum(y for y in ys) / sum(xs)
    return a, b


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dir')
    ap.add_argument('--k', type=float, default=1.5)
    ap.add_argument('--util', type=float, default=0.65)
    ap.add_argument('--p99', type=float, default=1.0)
    ap.add_argument('--standby', type=float, default=0.3)
    ap.add_argument('--kafka-rf', type=int, default=3)
    args = ap.parse_args()

    processes = sorted({re.sub(r'-\d+\.sql\.txt$', '', os.path.basename(p))
                        for p in glob.glob(os.path.join(args.dir, '*-[0-9]*.sql.txt'))})
    for proc in processes:
        rows = []
        for p in glob.glob(os.path.join(args.dir, f'{proc}-[0-9]*.sql.txt')):
            rate = int(re.search(r'-(\d+)\.sql\.txt$', p).group(1))
            r = parse_summary_row(p)
            if r is None:
                continue
            r['rate'] = rate
            r['cpu'] = parse_cpu(p.replace('.sql.txt', '.cpu.txt'))
            r['db'] = parse_db(p.replace('.sql.txt', '.db.txt'))
            r['sustained'] = (r['per_s'] >= 0.97 * rate and r['not_finished'] == 0 and r['p99'] < args.p99)
            rows.append(r)
        rows.sort(key=lambda r: r['rate'])
        print(f'\n## {proc}\n')
        print('| offered /s | completed /s | p50 s | p99 s | CPU cores | sustained |')
        print('|---:|---:|---:|---:|---:|:-:|')
        for r in rows:
            print(f"| {r['rate']} | {r['per_s']:.1f} | {r['p50']:.3f} | {r['p99']:.3f} | {sum(r['cpu'].values()):.2f} | {'yes' if r['sustained'] else 'no'} |")
        ok = [r for r in rows if r['sustained']]
        if not ok:
            print('\nNo sustained rate — nothing to fit.')
            continue
        services = sorted({s for r in ok for s in r['cpu']})
        tiers = {}
        total_a = total_b = 0.0
        for s in services:
            xs = [r['per_s'] for r in ok]
            ys = [r['cpu'].get(s, 0.0) for r in ok]
            a, b = fit(xs, ys)
            t = GROUPS.get(s, s)
            ta, tb = tiers.get(t, (0.0, 0.0))
            tiers[t] = (ta + a, tb + b)
            total_a += a
            total_b += b
        print(f"\nSustained up to {max(r['per_s'] for r in ok):.0f} /s (p99 < {args.p99} s). Fit over {len(ok)} rate(s):\n")
        print('| tier | idle cores | ms CPU per instance | share |')
        print('|---|---:|---:|---:|')
        for t, (a, b) in sorted(tiers.items(), key=lambda kv: -kv[1][1]):
            print(f'| {t} | {a:.2f} | {b * 1000:.1f} | {b / total_b:.0%} |')
        print(f'| **total** | {total_a:.2f} | **{total_b * 1000:.1f}** | |')

        dbs = {}
        for r in ok:
            for srv, (x, w) in r['db'].items():
                dbs.setdefault(srv, []).append((x, w))
        if dbs:
            print('\n| database server | transactions / instance | WAL KB / instance |')
            print('|---|---:|---:|')
            for srv, v in dbs.items():
                print(f'| {srv} | {statistics.mean(x for x, _ in v):.1f} | {statistics.mean(w for _, w in v):.1f} |')

        print(f'\nProjection (k = {args.k} vCPU per laptop core, sized for {args.util:.0%} CPU):\n')
        print('| target PI/s | laptop cores | cloud vCPU | with HA | of which Core silos | Journal | Read model | Kafka |')
        print('|---:|---:|---:|---:|---:|---:|---:|---:|')
        for T in TARGETS:
            cores = total_a + T * total_b
            vcpu = cores * args.k / args.util

            def tier(name):
                a, b = tiers.get(name, (0.0, 0.0))
                return (a + T * b) * args.k / args.util
            ha = vcpu + args.standby * tier("Journal (Citus)") + 0.75 * (args.kafka_rf - 1) * tier("Kafka")
            print(f'| {T:,} | {cores:.1f} | {vcpu:.0f} | {ha:.0f} | {tier("Core silos"):.0f} | {tier("Journal (Citus)"):.0f} | '
                  f'{tier("Read model (PG)"):.0f} | {tier("Kafka"):.0f} |')


if __name__ == '__main__':
    sys.exit(main())
