---
title: Upgrading
description: How to move a Fleans installation to a new release before 1.0 — a new release starts on fresh, empty stores — and what rolling restarts are still for.
---

Until 1.0.0, Fleans has **no upgrade path between releases**. Database schemas, journal events,
grain state and stream payloads change in place from one release to the next, and nothing converts
what an older release wrote. **Moving to a new release means starting it on fresh, empty stores.**
Running instances, deployed definitions, history and pending timers of the old release do not
carry over.

Pin every image and the Helm chart to one release, and change them together. Mixing engine
images of two releases in one cluster is not supported.

## What has to start empty

Every store the cluster uses:

| Store | Make it fresh by |
| --- | --- |
| PostgreSQL `fleans` database (and `fleans-journal`, `fleans-cluster`, `fleans-reminders` when you use them) | dropping and recreating the databases, or deleting the volume of a bundled server. A read replica (`fleans-query`) follows its primary. |
| Redis (membership, timers, stream queues by default) | recreating the bundled Redis container (`docker compose down`, or a new Helm Redis pod without a volume); on a Redis with a volume or a managed one, `FLUSHDB` or a new instance. |
| Kafka streams | deleting the topics (default prefix `fleans-`) and the consumer group, or setting a new `Fleans__Streaming__Kafka__TopicPrefix`. |
| Azure Queue streams | deleting the queues (default `fleans-stream-0` … `fleans-stream-7`), or listing new `Fleans__Streaming__AzureQueue__QueueNames__N`. |
| Databases of the ready-made plugins (e.g. `camunda_external_tasks`) | dropping and recreating them. The plugins are released with the engine. |

Fleans creates every table at startup, so each database only has to exist.

## Before you switch

1. **Stop starting new instances.** Take REST traffic away from the old cluster.
2. **Let running instances finish**, or cancel them. Whatever is still running on the old release
   is gone with its stores; start it again on the new release if you need it.
3. **Back up the old stores** if you need the history, for example for audits — see
   [Backup and restore](/fleans/guides/backup-restore/).
4. **Keep your `.bpmn` files.** Deployed definitions live in the old database. Deploy them again
   on the new release.

To switch without downtime, run the new release as a second cluster next to the old one: its own
databases, Redis or Kafka, and a different `Orleans__ClusterId`. Send new starts to the new
cluster, let the old one drain, then remove it.

## Docker Compose bundle

```bash
cd fleans                    # the directory of the old bundle
docker compose down -v       # stops the stack and deletes its Postgres volume

cd ..
gh release download v0.15.0 --repo nightBaker/fleans -p 'docker-compose-*.zip'
unzip docker-compose-v0.15.0.zip -d fleans-0.15.0
cd fleans-0.15.0
docker compose up -d
```

Copy your `docker-compose.override.yaml` (if you have one) and your changes to `.env` into the new
directory, and check them against the new bundle: variable names can change between releases. With
an external PostgreSQL, drop and recreate its databases before `up`. See
[Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/).

## Helm chart

`helm uninstall` keeps the bundled PostgreSQL's volume and its password `Secret` on purpose.
Delete them too, then install the new chart with your values file:

```bash
helm uninstall fleans -n fleans
kubectl get pvc,secret -n fleans                 # find what the release left behind
kubectl delete pvc data-fleans-postgres-0 -n fleans
kubectl delete pvc data-fleans-redis-0 -n fleans # only with redis.persistentVolume.enabled=true
kubectl delete secret fleans-postgres -n fleans

gh release download v0.15.0 --repo nightBaker/fleans -p 'fleans-0.15.0.tgz'
helm install fleans ./fleans-0.15.0.tgz -n fleans -f values-prod.yaml
```

The names above are for a release called `fleans`. With external databases or Kafka, empty them
before `helm install`. See [Self-host with Helm](/fleans/guides/self-host-helm/).

## What rolling restarts are for

A rolling restart replaces silos one at a time while the cluster keeps serving. Use it within one
release: configuration changes, scaling, node maintenance, a re-pulled image of the same tag. Keep
`Orleans__ClusterId`, `Orleans__ServiceId`, the membership store and the reminder and stream
providers the same on old and new pods. What a restart does to running instances, and what clients
must retry, is on [High availability](/fleans/reference/high-availability/#rolling-restarts).

Some settings cannot change on a running cluster even within one release — stream queue counts,
`Fleans:Signals:BucketCount`, the Citus shard count, the clustering, reminder and stream providers.
The list, with what each one needs, is in
[Recommended for scale → What cannot change on a live cluster](/fleans/guides/topology-scalable/#what-cannot-change-on-a-live-cluster).

## From 1.0.0 on

The fresh-stores rule applies until 1.0.0. What changes with 1.0.0 is on the
[Road to 1.0.0](/fleans/roadmap/v1/).
