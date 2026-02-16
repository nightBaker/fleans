# Editable Process ID on BPMN Canvas — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Let users edit the BPMN process ID inline on the canvas via a click-to-edit overlay in the top-left corner.

**Architecture:** A fixed-position HTML overlay on the canvas shows the current process ID. Clicking it opens a `FluentTextField` for editing. On confirm, JS calls `modeling.updateProperties()` on the root process element. No backend changes — the ID already flows from BPMN XML through `BpmnConverter` to `ProcessDefinitionKey`.

**Tech Stack:** bpmn-js (existing), Blazor Server + JS interop (existing), Fluent UI Blazor (existing)

---

### Task 1: Add JS functions for process ID get/set

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js`

**Step 1: Add `getProcessId` and `updateProcessId` to `bpmnEditor.js`**

Add these two methods to the `window.bpmnEditor` object, after `updateElementProperty` (line 108) and before `replaceElement` (line 110):

```javascript
    getProcessId: function () {
        if (!this._modeler) return null;
        var canvas = this._modeler.get('canvas');
        var rootElement = canvas.getRootElement();
        return rootElement.businessObject.id;
    },

    updateProcessId: function (newId) {
        if (!this._modeler) return false;
        var canvas = this._modeler.get('canvas');
        var rootElement = canvas.getRootElement();
        var modeling = this._modeler.get('modeling');
        modeling.updateProperties(rootElement, { id: newId });
        return true;
    },
```

**Step 2: Verify no syntax errors**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds (JS isn't compiled, but ensures no other files broke)

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js
git commit -m "feat: add getProcessId/updateProcessId to bpmnEditor.js"
```

---

### Task 2: Add CSS for the process ID overlay

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/app.css`

**Step 1: Add `position: relative` to `.editor-content`**

The overlay needs a positioned parent. Find `.editor-content` (line 95-99) and add `position: relative`:

```css
.editor-content {
    display: flex;
    flex: 1;
    min-height: 0;
    position: relative;
}
```

**Step 2: Add `.process-id-overlay` styles**

Add these styles after the `.editor-canvas` block (after line 106):

```css
.process-id-overlay {
    position: absolute;
    top: 12px;
    left: 12px;
    z-index: 10;
    display: flex;
    align-items: center;
    gap: 6px;
}

.process-id-display {
    background: var(--neutral-layer-1);
    border: 1px solid var(--neutral-stroke-rest);
    border-radius: 4px;
    padding: 4px 10px;
    font-size: var(--type-ramp-base-font-size);
    font-weight: 600;
    cursor: pointer;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.1);
    transition: border-color 0.15s;
}

.process-id-display:hover {
    border-color: var(--accent-fill-rest);
}

.process-id-edit {
    background: var(--neutral-layer-1);
    border: 1px solid var(--neutral-stroke-rest);
    border-radius: 4px;
    padding: 2px 4px;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.1);
}

.process-id-error {
    color: #e50000;
    font-size: 12px;
    background: var(--neutral-layer-1);
    border-radius: 4px;
    padding: 2px 6px;
}
```

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/app.css
git commit -m "feat: add CSS for process ID overlay on canvas"
```

---

### Task 3: Add overlay markup and edit logic to Editor.razor

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Editor.razor`

**Step 1: Add state fields for process ID editing**

In the `@code` block, after the `selectedElement` field (line 131), add:

```csharp
    private string processIdDisplay = "";
    private string processIdEdit = "";
    private bool isEditingProcessId;
    private string? processIdError;
```

**Step 2: Add the overlay markup**

Inside the `<div class="editor-content">` (line 86), add the overlay **before** the `<div id="editor-canvas">` element:

```razor
        <div class="process-id-overlay">
            @if (isEditingProcessId)
            {
                <div class="process-id-edit">
                    <FluentTextField @bind-Value="processIdEdit"
                                     Autofocus="true"
                                     @onkeydown="OnProcessIdKeyDown"
                                     @onblur="ConfirmProcessIdEdit"
                                     Placeholder="Process ID"
                                     Style="width: 220px;" />
                </div>
                @if (processIdError != null)
                {
                    <span class="process-id-error">@processIdError</span>
                }
            }
            else if (!string.IsNullOrEmpty(processIdDisplay))
            {
                <span class="process-id-display" @onclick="StartProcessIdEdit" title="Click to edit process ID">
                    @processIdDisplay
                </span>
            }
        </div>
```

**Step 3: Add the edit handler methods**

In the `@code` block, after `OnElementReplaced` (line 189), add these methods:

```csharp
    private void StartProcessIdEdit()
    {
        processIdEdit = processIdDisplay;
        processIdError = null;
        isEditingProcessId = true;
    }

    private async Task OnProcessIdKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await ConfirmProcessIdEdit();
        }
        else if (e.Key == "Escape")
        {
            isEditingProcessId = false;
            processIdError = null;
        }
    }

    private async Task ConfirmProcessIdEdit()
    {
        if (!isEditingProcessId) return;

        var newId = processIdEdit.Trim();

        if (string.IsNullOrEmpty(newId))
        {
            processIdError = "ID cannot be empty";
            return;
        }

        if (newId.Contains(' '))
        {
            processIdError = "ID cannot contain spaces";
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newId, @"^[a-zA-Z_][\w.\-]*$"))
        {
            processIdError = "ID must start with a letter or underscore";
            return;
        }

        if (newId != processIdDisplay)
        {
            await JS.InvokeAsync<bool>("bpmnEditor.updateProcessId", newId);
            processIdDisplay = newId;
            processKey = newId;
        }

        isEditingProcessId = false;
        processIdError = null;
    }

    private async Task RefreshProcessId()
    {
        if (!editorInitialized) return;
        var id = await JS.InvokeAsync<string>("bpmnEditor.getProcessId");
        if (!string.IsNullOrEmpty(id))
        {
            processIdDisplay = id;
            processKey = id;
        }
    }
```

**Step 4: Wire up `RefreshProcessId` after every diagram load**

There are 3 places where a diagram gets loaded. Add a `RefreshProcessId()` call after each:

**(a) After `loadXml` for existing definition (line 154):**
Change:
```csharp
                    await JS.InvokeVoidAsync("bpmnEditor.loadXml", bpmnXml);
                    processKey = ProcessDefinitionKey;
```
To:
```csharp
                    await JS.InvokeVoidAsync("bpmnEditor.loadXml", bpmnXml);
                    await RefreshProcessId();
```

**(b) After `newDiagram` for new editor (line 167):**
Change:
```csharp
                await JS.InvokeVoidAsync("bpmnEditor.newDiagram");
```
To:
```csharp
                await JS.InvokeVoidAsync("bpmnEditor.newDiagram");
                await RefreshProcessId();
```

**(c) After `loadXml` in the import handler (line 214):**
Change:
```csharp
            var bpmnXml = await File.ReadAllTextAsync(file.LocalFile.FullName);
            await JS.InvokeVoidAsync("bpmnEditor.loadXml", bpmnXml);
            successMessage = $"Imported {file.Name}";
```
To:
```csharp
            var bpmnXml = await File.ReadAllTextAsync(file.LocalFile.FullName);
            await JS.InvokeVoidAsync("bpmnEditor.loadXml", bpmnXml);
            await RefreshProcessId();
            successMessage = $"Imported {file.Name}";
```

Note: `RefreshProcessId()` sets `processKey` internally, so remove the standalone `processKey = ProcessDefinitionKey;` on the existing definition load path (it's replaced by the `RefreshProcessId()` call).

**Step 5: Build**

Run: `dotnet build src/Fleans/Fleans.sln`
Expected: Build succeeds

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/Editor.razor
git commit -m "feat: add editable process ID overlay on BPMN canvas"
```

---

### Task 4: Manual smoke test

**Step 1: Run the full stack**

Run: `dotnet run --project src/Fleans/Fleans.Aspire`

**Step 2: Test new diagram**

1. Navigate to `/editor` (new diagram)
2. Verify "Process_1" appears in top-left overlay
3. Click it — should turn into a text field with "Process_1" pre-filled
4. Change to "MyProcess" and press Enter
5. Verify overlay shows "MyProcess"
6. Click Deploy — confirm dialog should show "MyProcess" as the process key

**Step 3: Test validation**

1. Click the process ID to edit
2. Clear the field and press Enter — should show "ID cannot be empty"
3. Type "has space" and press Enter — should show "ID cannot contain spaces"
4. Press Escape — should cancel editing

**Step 4: Test existing diagram**

1. Navigate to `/workflows` and click Edit on a deployed workflow
2. Verify the process ID overlay shows the correct key
3. Click to edit, change it, verify it updates

**Step 5: Test import**

1. In editor, click Import BPMN and load a `.bpmn` file
2. Verify the overlay updates to show the imported process's ID

**Step 6: Commit (if any fixes needed)**

```bash
git add -u
git commit -m "fix: address smoke test findings for process ID editor"
```
