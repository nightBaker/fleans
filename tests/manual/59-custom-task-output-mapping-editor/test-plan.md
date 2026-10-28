# Custom-Task Output Mapping Editor (#585)

Verifies the BPMN editor's properties panel exposes Output Mappings for a registered custom-task plugin (REST Caller), and that the plugin-switch confirm dialog gates on any `ioMapping` input (`Element.InputMappings`, which includes typed parameters) or output (`Element.OutputMappings`) so output authoring cannot be silently lost.

Regression home for #585.

## Prerequisites

- The REST Caller plugin host from [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) built (`dotnet build src/Fleans.RestCaller.Host` in that checkout) — the engine bundles no plugin.
- Aspire stack running with that host attached (from `src/Fleans/`): `FLEANS_PLUGIN_HOST_PROJECT=<abs path>/fleans-plugins/src/Fleans.RestCaller.Host/Fleans.RestCaller.Host.csproj dotnet run --project Fleans.Aspire`.
- Web UI reachable at `https://localhost:7124`.
- Plugin catalog contains at least one other registered plugin so step 5 can switch. If only RestCaller is present, register a second plugin or skip 5a–5c (they exercise the dialog gate only — the rest of the plan covers the primary fix).

## Steps

### Step 1 — Deploy fixture
- Web UI → **Workflows** → **Deploy**. Upload `rest-output-mapping.bpmn`.
- Verify the workflow appears in the list with status **Deployed**.

### Step 2 — Open in editor, verify typed inputs render
- Open `/editor`, paste the contents of `rest-output-mapping.bpmn` (or open the deployed copy).
- Click the `Fetch Slideshow` ServiceTask.
- Verify the **Plugin (custom task)** dropdown shows `REST Caller (rest-call)` selected.
- Verify the typed input editor (`CustomTaskParameterEditor`) renders fields like `URL`, `Method`, `Timeout` populated with `https://httpbin.org/json`, `GET`, `10`.

### Step 3 — Verify output mapping section is visible
- Scrolling below the typed input editor, verify:
  - An **info bar** appears with copy beginning *"Outputs are free-form — declare which keys from the plugin's response to capture as workflow variables..."* and referencing `__response`.
  - An **Input/Output mapping** section with an **Outputs** list (since #950 this shared section replaces the service task's own Output Mappings table; its **Inputs** list shows only inputs the plugin schema does not declare — none for this fixture).
  - Two existing rows from the fixture: `source=__response.body target=responseBody` and `source=__response.statusCode target=status`.
  - An **Add output** button.

### Step 4 — Author a new output mapping
- Click **Add output**. Verify an empty row appears (source/target text fields) with a red *Target must be a variable name* hint until the target is filled.
- In the new row, set `source = =__response.body.slideshow.title`, `target = title`.
- Click **Save / Deploy**. Verify the deployment succeeds.
- Close the editor; re-open the same workflow definition. Verify the **3rd** output row persists with the values just authored.

### Step 5 — Plugin-switch confirm dialog gate (requires second plugin in catalog)

#### 5a — typed-params trigger (existing path)
- On the same ServiceTask, change the `URL` typed-input field to a different value (any non-empty change). This populates `currentParameterValues`.
- Click the plugin dropdown and select a different registered plugin.
- Verify a confirm dialog appears titled **Replace plugin?** with body copy *"Switching plugins will clear all parameter values and output mappings on this service task. Continue?"*
- Click **Cancel**. Verify: typed-input values intact, output mappings intact, plugin dropdown reverts to REST Caller.

#### 5b — outputs-only trigger (new gate path)
- Click **Save / Deploy**, then re-open the workflow to start fresh (resets `currentParameterValues`).
- Select the ServiceTask. **Do NOT modify any typed input.**
- Click **Add output** and add a row (any non-empty values).
- Click the plugin dropdown and select a different registered plugin.
- Verify the confirm dialog **fires** (this is the new gate path — would not fire pre-fix).
- Click **Cancel**. Verify: output mapping rows intact, plugin dropdown reverts.

#### 5c — confirm-and-wipe
- Repeat 5b but click **OK / Confirm** instead of Cancel.
- Verify: the plugin selector now shows the new plugin, and the Outputs list is empty (no rows).
- Optional: re-open the workflow in the editor and confirm the Outputs list is empty — proxy for "no `<zeebe:output>` survives in the saved XML".

### Step 6 — End-to-end run
- Restore the fixture (re-deploy `rest-output-mapping.bpmn` if step 5c modified it).
- Start a workflow instance via **Workflows → Start**.
- Open the instance detail page. Verify:
  - The workflow reached the end event (status **Completed**).
  - Workflow variables include `responseBody` (object) and `status` (number, `200`) — i.e., the output mappings actually fired.
  - If step 4's `title` mapping was preserved, that variable should also be populated with the slideshow title from the httpbin response.

### Step 7 — Documentation cross-check
- Open `website/src/content/docs/concepts/custom-tasks.md` in the rendered docs site (or read the source). Verify the paragraph **Authoring in the editor** appears in the *Ready-made plugins* section, describing the typed-input editor + free-form output mapping behaviour and mentioning the reserved `__response` key.

## Reference

- Plugin handlers return their result under `__response` by convention; see `src/Fleans.Plugins.RestCaller/RestCallerHandler.cs` in [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) for the canonical example.
- Issue: #585 — *"can't to add output mapping for rest plugin in bpmn editor"*.
- PR: linked from the issue.
