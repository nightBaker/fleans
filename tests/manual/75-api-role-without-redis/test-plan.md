# 75 — REST-only `Api` role and a cluster without Redis

**Feature:** `Fleans:Role=Api` runs `Fleans.Api` as an Orleans client with no grains (REST only), and
every host registers the `orleans-redis` client only when a Redis-backed provider is configured (#911
follow-up). Fixture: `../02-script-tasks/script-variable-manipulation.bpmn` (process `script-variables`).

**Automated coverage (not repeated here):** `FleansRedisExtensionsTests` (when each host kind needs
Redis, keyed client + health check registered or not), `FleansLoadSheddingExtensionsTests`,
`GlobalExceptionHandlerTests` (gateway too busy → 503), the `helm-lint` workflow (api Deployment,
`redis.enabled=false` renders no `orleans-redis`, load-shedding env). The two-process run below is not
automated.

## Prerequisites

- `dotnet build` from `src/Fleans/`.
- A throwaway Postgres, no Redis anywhere:
  `docker run -d --rm --name fleans-75-pg -e POSTGRES_USER=fleans -e POSTGRES_PASSWORD=p -e POSTGRES_DB=fleans -p 55432:5432 postgres:16-alpine`
- Shared environment for both processes (`env VAR=… dotnet …`):

  ```
  ConnectionStrings__fleans=Host=localhost;Port=55432;Database=fleans;Username=fleans;Password=p
  Persistence__Provider=Postgres
  Fleans__Clustering__Provider=Postgres
  Fleans__Reminders__Provider=Postgres
  Fleans__Streaming__Provider=Memory
  Orleans__ClusterId=fleans-75
  Orleans__ServiceId=fleans-75
  ```

## Steps

1. **Silo without Redis.** Run `Fleans.Api` with `Fleans__Role=Combined` on `--urls http://127.0.0.1:58081`.
   - [ ] `GET http://127.0.0.1:58081/health` → `200 Healthy` (no Redis check, no Redis connection errors in the log).
2. **API client.** Run a second `Fleans.Api` with `Fleans__Role=Api` on `--urls http://127.0.0.1:58082`.
   - [ ] Log shows `Started client with address … sys.client/…` and no silo start-up lines.
   - [ ] `GET http://127.0.0.1:58082/health` → `200 Healthy`.
3. **REST through the client.**
   - [ ] `POST :58082/Definitions/deploy` with `{"BpmnXml": "<contents of the fixture>"}` → `{"processDefinitionKey":"script-variables","version":1}`.
   - [ ] `POST :58082/Execution/start` with `{"WorkflowId":"script-variables"}` → a `workflowInstanceId`.
   - [ ] `GET :58082/Instances/{id}/state` → `isCompleted: true`, `isFailed: false`.
   - [ ] `GET :58082/custom-tasks` lists `rest-call` with a `combined-…` silo name only (the Api host registered no plugin).
4. **Role validation.** Start `Fleans.Api` with `Fleans__Role=Bogus`.
   - [ ] Startup fails with `Fleans:Role must be one of …`, and the list includes `'Api'`.
5. Cleanup: stop both processes, `docker stop fleans-75-pg`.
