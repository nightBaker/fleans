# Manual Test Plan #39 — REST Caller plugin

Verifies the `rest-call` custom-task end-to-end: GET happy path; POST with body+headers; failure routing on non-success; timeout; idempotency-key opt-in.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Clean dev DB.
- Web UI reachable at `https://localhost:7124`; API origin `https://localhost:7140`.
- `GET https://localhost:7140/custom-tasks` returns at least one entry with `taskType: "rest-call"` (proves the plugin is registered).
- A reachable HTTP test endpoint. Use `https://httpbin.org/anything` or a local stub. The fixture (`rest-call.bpmn`) is `=apiUrl`-driven so no BPMN edits needed.

## Scenario 1 — GET happy path

1. **Deploy.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/deploy \
     -H "Content-Type: application/json" \
     -d "{\"BpmnXml\": $(jq -Rs . < tests/manual/38-rest-caller/rest-call.bpmn)}"
   ```
2. **Start.**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"rest-call-demo","Variables":{
            "apiUrl":"https://httpbin.org/get",
            "apiHeaders":{"Accept":"application/json"}
         }}'
   ```
3. **Verify.** State endpoint shows `isCompleted: true` within ~5s; variable projection contains `apiBody` (the parsed JSON body) and `apiStatus = 200`.

## Scenario 2 — POST with body + headers

Edit the fixture to set `method=POST` and add `body` input. Or send a fresh deploy with that variant. Then:

```bash
curl -k -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"rest-call-demo","Variables":{
         "apiUrl":"https://httpbin.org/post",
         "apiHeaders":{"Content-Type":"application/json","X-Foo":"bar"}
      }}'
```

Verify httpbin echoed the headers + body in its response.

## Scenario 3 — Non-success routes via boundary error event

Use a workflow variant with a boundary error event on `callApi` that catches `errorCode="404"` and routes to a recovery branch. Hit a 404 endpoint:

```bash
curl -k -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"rest-call-demo","Variables":{
         "apiUrl":"https://httpbin.org/status/404",
         "apiHeaders":{}
      }}'
```

Verify the recovery branch ran (either via state endpoint or by examining variables it set). The activity should show `errorState.code = "404"` in the snapshot.

## Scenario 4 — Timeout

Hit a slow endpoint (httpbin.org/delay/30) with a workflow variant where `timeoutSec=2`. Verify activity Fails with `errorState.code = "504"` within ~3s.

## Scenario 5 — Idempotency-key

Add `<zeebe:input source="X-Request-Id" target="idempotencyKeyHeader" />` to the fixture. Hit `https://httpbin.org/anything` and verify the response echoes `X-Request-Id: <some-guid>` (httpbin reflects request headers).

## Expected outcomes (checklist)

- [ ] Scenario 1: deploy succeeds; workflow completes; `apiStatus=200`; `apiBody` is the JSON object from httpbin.
- [ ] Scenario 2: server received body + custom headers (verify via response echo).
- [ ] Scenario 3: activity Fails with `code="404"`; boundary error event catches and routes to recovery.
- [ ] Scenario 4: activity Fails with `code="504"` within `timeoutSec ± 1s`.
- [ ] Scenario 5: request carried `X-Request-Id: <activityInstanceId-guid>`.

## Known limitations (v1)

- `headers` and `successCodes` must come from workflow variables; literal map / list authoring inside BPMN is not supported until sub-issue C ships.
- No OAuth / mTLS / cert auth — pre-compute bearer tokens in a preceding script task and pass via `headers["Authorization"]`.
- No HTTP-level retry — workflow author owns retry semantics via boundary error events.
