# Manual Test Plan #78 — Editor AI assistant panel

Verifies #1015 and #1024: the BPMN editor's AI assistant panel against a stub AG-UI backend implementing the `Fleans.Assistant.Contracts` contract (#1054) — streaming text, proposals validated by the engine's BPMN converter with up to 2 automatic fix requests, direct apply with a per-tab change history, new-tab / replace-current apply, error surfacing, no deploy from the panel, and the panel staying hidden without an endpoint.

## Prerequisites
- Stub backend (this folder, standard library only): `python3 stub-backend.py --mode valid` — listens on `http://localhost:5300/`, prints one line per request (message count, `currentXml`, plugin task types, engine version, `Authorization`). Restart it with another `--mode` per scenario.
- Aspire stack pointed at it: from `src/Fleans/`, `FLEANS_ASSISTANT_ENDPOINT=http://localhost:5300 dotnet run --project Fleans.Aspire`. Optionally add `FLEANS_EXTERNAL_WORKER_PROJECT=$PWD/Fleans.E2E.ExternalWorker/Fleans.E2E.ExternalWorker.csproj` so the plugin catalog is not empty.
- Admin UI → **Editor** (`/editor`), a wide window (≥ 1280 px).

## Scenario 1 — Hidden without an endpoint
1. Run the stack without `FLEANS_ASSISTANT_ENDPOINT`. Open `/editor` — expect no **AI assistant** button in the toolbar.
2. Start Web with `Fleans__Assistant__Endpoint=not-a-url` — expect startup to fail with `Fleans:Assistant:Endpoint must be an absolute http(s) URL.`

## Scenario 2 — Generate into a new tab (`--mode valid`)
1. Click **AI assistant** — the inspector shows the chat with the privacy note.
2. Type `approval process with a 4h escalation timer`, **Send**. The reply streams in; a card *Diagram proposed · passes the BPMN check* appears. The stub logs `currentXml=null` (blank tab), the plugin task types, a non-empty `engineVersion`.
3. The proposal is already applied to the blank tab (scenario 8). **Open in new tab** — a tab `manual_ai_approval` opens with a draft dot; the diagram is laid out (start → Approve request → Approved, a timer boundary → Escalated) although the XML has no `bpmndi`. The chat continues in the new tab.
4. Send `add a timer to the approval task` — the stub logs `currentXml=yes`, and the previous proposal arrives as a fenced `xml` block in the last assistant message.
5. Confirm there is no deploy action in the panel; **Deploy** in the toolbar opens the usual checklist.

## Scenario 3 — Replace current (auto-apply off)
1. Turn **Apply changes automatically** off; reload — it stays off.
2. In a tab with a diagram, send a request — the diagram doesn't change; the proposal offers **Replace current** and **Open in new tab**.
3. **Replace current** — no confirmation; the tab shows the proposal and is marked dirty. Ctrl Z does not bring the old diagram back; **Undo last change** does.

## Scenario 4 — Automatic fix loop (`--mode fix`, then `--mode invalid`)
1. `fix`: send a request — the panel shows *The proposal did not pass the engine's BPMN check — asked the assistant to fix it*, then the valid proposal card. The stub logs two requests, the second with `last=fix: ...`.
2. `invalid`: send a request — three requests reach the stub (1 + 2 fixes), each rejected proposal is marked *Proposal rejected by the BPMN check*, and the error bar reads *The proposed diagram still fails the engine's BPMN check after 2 automatic fixes* with the converter's message. No apply buttons appear, and with auto-apply on the diagram never changes.

## Scenario 5 — Errors keep the editor usable
Restart the stub per mode and send one request each; the error bar shows, the diagram and tabs keep working:
| Mode | Panel shows |
|---|---|
| `401` | *The assistant rejected your sign-in…* + `Stub answered 401` |
| `429` | *The assistant is busy right now…* + `Stub answered 429` |
| `500` | *The assistant service is unavailable.* |
| `error` | *The assistant could not answer.* + `The stub refuses this request.` (the streamed `Thinking...` stays) |
| stub stopped | *The assistant service is unavailable.* |
| `slow` + **Stop** | the request stops, no error; switching tabs mid-answer also stops it |

## Scenario 6 — Mode and language
1. Switch to **Simulation** or **History** — the mode's panel replaces the chat; in **XML** mode **Send** is disabled with *Switch to Modeling mode…*.
2. Settings → language **Русский** — the panel texts are Russian (*ИИ-ассистент*, *Открыть в новой вкладке*, …).

## Scenario 7 — Token forwarding (auth on)
With `FLEANS_E2E_AUTH=true` (Keycloak, sign in as `alice`) and the stub in `valid` mode, send a request — the stub logs `auth=Bearer`. Without auth it logs `auth=none`.

## Scenario 8 — Apply directly and undo (`--mode valid`, auto-apply on)
1. In a blank tab, send a request — no dialog; the diagram appears in the same tab with a draft dot, and the message shows *Applied to this tab*.
2. **Undo last change** — the blank diagram is back; the message offers **Apply**, and applying brings the proposal back.
3. Send a request, then switch to **XML** before the answer arrives (use `--mode slow`) — the proposal waits with a disabled **Apply** and *Switch to Modeling mode…*. Back in **Modeling**, **Apply** works.

## Scenario 9 — History keeps every state
1. Apply a proposal, then add an element by hand. Open **History** — the list has *Before the changes* and *Assistant change* (with your request), the latter marked *Current*.
2. **Restore** *Before the changes* — the blank diagram returns, and *Manual edits* and *Restored revision 1* are now in the list. **Restore** *Manual edits* — your hand-added element is back.
3. **Show message** on an assistant entry scrolls to and focuses its chat message.
4. Tab through the list with the keyboard: every **Restore** is reachable and works with Enter.
5. Open a second diagram tab: it has its own (empty) history. **Open in new tab** starts a history with the proposal as its only entry.

## Scenario 10 — History storage and limits
1. Reload the page (accept the unsaved-changes prompt) — **History** is unchanged, with no new *Manual edits* entry on the next change. (The *Applied* mark coming back on a stored chat's message needs a backend that keeps chats, which this stub doesn't; `EditorAiAssistantTests` covers it.)
2. Copy the `/editor` URL to a new browser tab — the diagram tabs are there, but no **History**.
3. Close a diagram tab — in DevTools → Application → Session Storage, its `fleans.editor.assistant.history.v1.<tabId>` key is gone. Close the browser tab — the key is gone with it.
4. **Cap:** repeat apply / hand edit / undo until there are over 50 entries — the list stays at 50 and shows *N oldest revisions were dropped*.
5. **Quota:** in DevTools run `Storage.prototype.setItem = () => { throw new DOMException('full', 'QuotaExceededError') }`, then apply a proposal — it is applied, the panel warns *This browser could not store the history*, and **Undo last change** and **History** still work. Web logs EventId 13084.
6. Check the panel at 390, 768 and 1280 px: the list doesn't scroll sideways. (At 768 px and below the editor is view-only and hides the toolbar button: open the panel in a wide window, then narrow it.) Language **Русский**: *История*, *Отменить последнее изменение*, *Применить*.
7. Confirm with the stub's request log that no request carries history entries: only the open diagram as `currentXml`.

## Pass criteria
All scenarios behave as described; Web logs show EventIds 13070/13071 per call (13075–13077 for the fix loop, 13072 for errors, 13083 per recorded revision) and never a prompt or diagram.
