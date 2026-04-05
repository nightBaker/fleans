# UserTask Properties Panel Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a properties panel for UserTask elements in the BPMN editor, allowing users to set Assignee, CandidateGroups, CandidateUsers, and ExpectedOutputVariables.

**Architecture:** Extend the existing `ElementPropertiesPanel.razor` with a UserTask section following the same pattern as ScriptTask/CallActivity. Add a reusable `TagInput.razor` component for chip-style multi-value fields. Extend `bpmnEditor.js` to extract and persist UserTask properties using Camunda attributes and Fleans extension elements.

**Tech Stack:** Blazor Server (Fluent UI), bpmn-js, JavaScript interop

---

### Task 1: Add UserTask Properties to BpmnElementData and JS Extraction

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor:501-523` (BpmnElementData class)
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js:43-142` (_extractElementData)
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/fleansModdleExtension.js` (add ExpectedOutputs types)

**Step 1: Add properties to BpmnElementData**

In `ElementPropertiesPanel.razor`, add these properties to the `BpmnElementData` class (after line 522, before the closing `}`):

```csharp
public string Assignee { get; set; } = "";
public List<string> CandidateGroups { get; set; } = [];
public List<string> CandidateUsers { get; set; } = [];
public List<string> ExpectedOutputVariables { get; set; } = [];
public List<string> AvailableVariables { get; set; } = [];
```

**Step 2: Add UserTask extraction in bpmnEditor.js**

In `_extractElementData`, add to the `data` object initialization (after line 65, `isInterrupting: true`):

```javascript
assignee: '',
candidateGroups: [],
candidateUsers: [],
expectedOutputVariables: [],
availableVariables: []
```

After the `bpmn:CallActivity` block (after line 93), add:

```javascript
if (bo.$type === 'bpmn:UserTask') {
    var attrs = bo.$attrs || {};
    data.assignee = attrs['camunda:assignee'] || '';
    var groups = attrs['camunda:candidateGroups'] || '';
    data.candidateGroups = groups ? groups.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];
    var users = attrs['camunda:candidateUsers'] || '';
    data.candidateUsers = users ? users.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];

    if (bo.extensionElements && bo.extensionElements.values) {
        bo.extensionElements.values.forEach(function (ext) {
            if (ext.$type === 'fleans:ExpectedOutputs' && ext.outputs) {
                ext.outputs.forEach(function (output) {
                    if (output.name) data.expectedOutputVariables.push(output.name);
                });
            }
        });
    }
}
```

After the `BoundaryEvent` block (after line 139), add available variables extraction:

```javascript
// Collect available variable names from ScriptTasks in the diagram
if (this._modeler) {
    var registry = this._modeler.get('elementRegistry');
    var vars = new Set();
    registry.forEach(function (el) {
        var elBo = el.businessObject;
        if (elBo.$type === 'bpmn:ScriptTask' && elBo.script) {
            var matches = elBo.script.match(/variables\.(\w+)/g);
            if (matches) {
                matches.forEach(function (m) { vars.add(m.replace('variables.', '')); });
            }
        }
    });
    data.availableVariables = Array.from(vars).sort();
}
```

**Step 3: Add ExpectedOutputs types to fleansModdleExtension.js**

In `fleansModdleExtension.js`, add two new types to the `types` array (after the OutputMapping type, before line 60):

```javascript
{
    name: "ExpectedOutputs",
    superClass: ["Element"],
    properties: [
        {
            name: "outputs",
            type: "Output",
            isMany: true
        }
    ]
},
{
    name: "Output",
    superClass: ["Element"],
    properties: [
        {
            name: "name",
            type: "String",
            isAttr: true
        }
    ]
}
```

**Step 4: Build and verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor \
       src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js \
       src/Fleans/Fleans.Web/wwwroot/js/fleansModdleExtension.js
git commit -m "feat: add UserTask property extraction in JS and data model (#186)"
```

---

### Task 2: Add JS Methods to Persist UserTask Properties

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js:170-199` (add UserTask update methods)

**Step 1: Extend updateElementProperty for UserTask attributes**

In `updateElementProperty` (line 170), add a new branch before the final `else` at line 194. After the `cancelActivity` branch (line 193):

```javascript
} else if (propertyName === 'camunda:assignee' || propertyName === 'camunda:candidateGroups' || propertyName === 'camunda:candidateUsers') {
    var bo = element.businessObject;
    if (!bo.$attrs) bo.$attrs = {};
    if (value && value.trim() !== '') {
        bo.$attrs[propertyName] = value;
    } else {
        delete bo.$attrs[propertyName];
    }
    return;
```

**Step 2: Add updateExpectedOutputs method**

After `updateElementProperty` (after line 199), add a new method:

```javascript
updateExpectedOutputs: function (elementId, variableNames) {
    if (!this._modeler) return;

    var elementRegistry = this._modeler.get('elementRegistry');
    var modeling = this._modeler.get('modeling');
    var moddle = this._modeler.get('moddle');
    var element = elementRegistry.get(elementId);
    if (!element) return;

    var bo = element.businessObject;

    // Remove existing ExpectedOutputs
    var existingExtensions = [];
    if (bo.extensionElements && bo.extensionElements.values) {
        existingExtensions = bo.extensionElements.values.filter(function (ext) {
            return ext.$type !== 'fleans:ExpectedOutputs';
        });
    }

    if (variableNames && variableNames.length > 0) {
        var outputs = variableNames.map(function (name) {
            return moddle.create('fleans:Output', { name: name });
        });
        var expectedOutputs = moddle.create('fleans:ExpectedOutputs', { outputs: outputs });

        if (!bo.extensionElements) {
            var extElements = moddle.create('bpmn:ExtensionElements', { values: [] });
            modeling.updateProperties(element, { extensionElements: extElements });
        }
        bo.extensionElements.values = existingExtensions.concat([expectedOutputs]);
    } else {
        if (bo.extensionElements) {
            bo.extensionElements.values = existingExtensions;
        }
    }
},
```

**Step 3: Verify no syntax errors**

Open the Aspire app (`dotnet run --project src/Fleans/Fleans.Aspire`) and check browser console for JS errors. Navigate to editor page.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js
git commit -m "feat: add JS methods to persist UserTask properties (#186)"
```

---

### Task 3: Create Reusable TagInput Component

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Shared/TagInput.razor`

**Step 1: Create the TagInput component**

```razor
@using Microsoft.FluentUI.AspNetCore.Components

<div class="tag-input-container">
    @foreach (var tag in Tags)
    {
        <FluentBadge Appearance="Appearance.Neutral" Style="margin: 2px;">
            @tag
            @if (!Disabled)
            {
                <span class="tag-remove" @onclick="() => RemoveTag(tag)" style="cursor: pointer; margin-left: 4px;">&times;</span>
            }
        </FluentBadge>
    }

    @if (!Disabled)
    {
        <div style="position: relative; width: 100%;">
            <FluentTextField @bind-Value="inputValue"
                             @onkeydown="OnKeyDown"
                             @oninput="OnInput"
                             Placeholder="@Placeholder"
                             Style="width: 100%; margin-top: 4px;" />
            @if (showSuggestions && filteredSuggestions.Count > 0)
            {
                <div class="tag-suggestions">
                    @foreach (var suggestion in filteredSuggestions)
                    {
                        <div class="tag-suggestion-item" @onclick="() => AddTag(suggestion)">@suggestion</div>
                    }
                </div>
            }
        </div>
    }
</div>

@code {
    [Parameter, EditorRequired] public List<string> Tags { get; set; } = [];
    [Parameter] public EventCallback<List<string>> TagsChanged { get; set; }
    [Parameter] public List<string>? Suggestions { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public string Placeholder { get; set; } = "Type and press Enter";

    private string inputValue = "";
    private bool showSuggestions;
    private List<string> filteredSuggestions = [];

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(inputValue))
        {
            await AddTag(inputValue.Trim());
        }
    }

    private void OnInput(ChangeEventArgs e)
    {
        inputValue = e.Value?.ToString() ?? "";
        if (Suggestions is not null && !string.IsNullOrEmpty(inputValue))
        {
            filteredSuggestions = Suggestions
                .Where(s => s.Contains(inputValue, StringComparison.OrdinalIgnoreCase) && !Tags.Contains(s))
                .Take(8)
                .ToList();
            showSuggestions = filteredSuggestions.Count > 0;
        }
        else
        {
            showSuggestions = false;
            filteredSuggestions = [];
        }
    }

    private async Task AddTag(string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag) && !Tags.Contains(tag))
        {
            Tags.Add(tag);
            await TagsChanged.InvokeAsync(Tags);
        }
        inputValue = "";
        showSuggestions = false;
        filteredSuggestions = [];
    }

    private async Task RemoveTag(string tag)
    {
        Tags.Remove(tag);
        await TagsChanged.InvokeAsync(Tags);
    }
}
```

**Step 2: Add CSS for suggestions dropdown**

In `src/Fleans/Fleans.Web/wwwroot/app.css`, add at the end:

```css
/* Tag Input */
.tag-input-container {
    display: flex;
    flex-wrap: wrap;
    gap: 2px;
}

.tag-suggestions {
    position: absolute;
    top: 100%;
    left: 0;
    right: 0;
    background: var(--neutral-layer-floating);
    border: 1px solid var(--neutral-stroke-rest);
    border-radius: 4px;
    z-index: 10;
    max-height: 160px;
    overflow-y: auto;
}

.tag-suggestion-item {
    padding: 6px 12px;
    cursor: pointer;
}

.tag-suggestion-item:hover {
    background: var(--neutral-fill-stealth-hover);
}
```

**Step 3: Build**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Shared/TagInput.razor \
       src/Fleans/Fleans.Web/wwwroot/app.css
git commit -m "feat: add reusable TagInput component with autocomplete (#186)"
```

---

### Task 4: Add UserTask Section to ElementPropertiesPanel

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor`

**Step 1: Add UserTask fields to local state**

After `private bool isInterrupting = true;` (line 208), add:

```csharp
private string assignee = "";
private List<string> candidateGroups = [];
private List<string> candidateUsers = [];
private List<string> expectedOutputVariables = [];
```

**Step 2: Initialize from Element in OnParametersSet**

After `isInterrupting = Element.IsInterrupting;` (line 252), add:

```csharp
assignee = Element.Assignee;
candidateGroups = new List<string>(Element.CandidateGroups);
candidateUsers = new List<string>(Element.CandidateUsers);
expectedOutputVariables = new List<string>(Element.ExpectedOutputVariables);
```

**Step 3: Add UserTask UI section**

After the Signal section closing `}` (line 185), before `</FluentStack>` (line 186), add:

```razor
@if (Element.Type == "bpmn:UserTask")
{
    <div>
        <FluentLabel Typo="Typography.Body" Weight="FontWeight.Bold">Assignee</FluentLabel>
        <FluentTextField Value="@assignee" @oninput="OnAssigneeInput" @onchange="OnAssigneeChange"
                         Disabled="@ReadOnly" Placeholder="e.g. john.doe" Style="width: 100%;" />
    </div>

    <div>
        <FluentLabel Typo="Typography.Body" Weight="FontWeight.Bold">Candidate Groups</FluentLabel>
        <TagInput Tags="@candidateGroups" TagsChanged="OnCandidateGroupsChanged"
                  Disabled="@ReadOnly" Placeholder="Type group and press Enter" />
    </div>

    <div>
        <FluentLabel Typo="Typography.Body" Weight="FontWeight.Bold">Candidate Users</FluentLabel>
        <TagInput Tags="@candidateUsers" TagsChanged="OnCandidateUsersChanged"
                  Disabled="@ReadOnly" Placeholder="Type user and press Enter" />
    </div>

    <div>
        <FluentLabel Typo="Typography.Body" Weight="FontWeight.Bold">Expected Output Variables</FluentLabel>
        <TagInput Tags="@expectedOutputVariables" TagsChanged="OnExpectedOutputsChanged"
                  Suggestions="@Element.AvailableVariables" Disabled="@ReadOnly"
                  Placeholder="Type variable name" />
        <FluentLabel Style="color: var(--neutral-foreground-hint); margin-top: 4px; font-size: 12px;">
            Variables the user task is expected to set on completion
        </FluentLabel>
    </div>
}
```

**Step 4: Add event handlers**

After the existing signal event handlers in the `@code` block, add:

```csharp
private void OnAssigneeInput(ChangeEventArgs e) => assignee = e.Value?.ToString() ?? "";
private async Task OnAssigneeChange(ChangeEventArgs e)
{
    assignee = e.Value?.ToString() ?? "";
    await JS.InvokeVoidAsync("bpmnEditor.updateElementProperty", Element.Id, "camunda:assignee", assignee);
}

private async Task OnCandidateGroupsChanged(List<string> tags)
{
    candidateGroups = tags;
    var csv = string.Join(",", tags);
    await JS.InvokeVoidAsync("bpmnEditor.updateElementProperty", Element.Id, "camunda:candidateGroups", csv);
}

private async Task OnCandidateUsersChanged(List<string> tags)
{
    candidateUsers = tags;
    var csv = string.Join(",", tags);
    await JS.InvokeVoidAsync("bpmnEditor.updateElementProperty", Element.Id, "camunda:candidateUsers", csv);
}

private async Task OnExpectedOutputsChanged(List<string> tags)
{
    expectedOutputVariables = tags;
    await JS.InvokeVoidAsync("bpmnEditor.updateExpectedOutputs", Element.Id, tags);
}
```

**Step 5: Build**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor
git commit -m "feat: add UserTask properties section to editor panel (#186)"
```

---

### Task 5: Manual Verification

**Step 1: Start the Aspire stack**

Run: `dotnet run --project src/Fleans/Fleans.Aspire`

**Step 2: Open editor and test**

1. Navigate to the Web UI editor page
2. Create or import a BPMN with a UserTask
3. Click the UserTask element
4. Verify the right panel shows: Assignee, Candidate Groups, Candidate Users, Expected Output Variables
5. Set Assignee to "john.doe" — verify it persists (download XML, check `camunda:assignee`)
6. Add candidate groups "managers", "reviewers" — verify chips appear, XML has `camunda:candidateGroups="managers,reviewers"`
7. Add candidate users "alice", "bob" — same verification
8. Add expected output variable — verify autocomplete shows variables from ScriptTasks if any exist
9. Add "approved", "comment" as expected outputs — download XML, verify `<fleans:expectedOutputs>` extension elements
10. Remove a chip — verify it's removed from XML
11. Import a BPMN with existing UserTask properties — verify fields are populated

**Step 3: Commit any fixes**

If any issues found, fix and commit.

**Step 4: Final commit**

```bash
git commit -m "test: verify UserTask properties panel manually (#186)"
```
