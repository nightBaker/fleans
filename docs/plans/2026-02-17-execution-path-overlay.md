# Execution Path Overlay Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace activity coloring with semi-transparent overlay arrows on traversed sequence flows, showing the execution path through the BPMN diagram.

**Architecture:** Blazor sorts activity instances by `CreatedAt` and passes the ordered activity ID list to JavaScript. JS builds a map of all BPMN sequence flows, then for each activity finds its predecessor by searching backwards for a connecting sequence flow. Traversed flows are drawn as semi-transparent SVG paths on a custom canvas layer.

**Tech Stack:** bpmn-js (canvas API, elementRegistry), Blazor JS interop, SVG, CSS animations

---

### Task 1: Update CSS — remove completed marker, add pulsing animation

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/app.css:54-63`

**Step 1: Replace CSS styles**

Remove the `.bpmn-completed` rule entirely. Update `.bpmn-active` to use a pulsing border animation instead of a static fill. Add the keyframes.

Replace lines 54–63 in `app.css` with:

```css
.bpmn-active .djs-visual > :nth-child(1) {
    stroke: #3b82f6 !important;
    stroke-width: 3px !important;
    animation: pulse-border 1.5s ease-in-out infinite;
}

@keyframes pulse-border {
    0%, 100% { stroke-opacity: 1; }
    50% { stroke-opacity: 0.4; }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Fleans/Fleans.Web/Fleans.Web.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/app.css
git commit -m "style: replace completed marker with pulsing active border"
```

---

### Task 2: Update bpmnViewer.js — add highlightExecutionPath, update highlight

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`

**Step 1: Update the `highlight` method**

The `highlight` method currently applies both `bpmn-completed` and `bpmn-active` markers. Update it to only apply `bpmn-active` (remove all `bpmn-completed` logic). Keep removing both markers in the clear step for backwards safety.

Replace the `highlight` method (lines 54–81) with:

```javascript
highlight: function (activeIds) {
    if (!this._viewer) return;

    const canvas = this._viewer.get('canvas');
    const elementRegistry = this._viewer.get('elementRegistry');

    elementRegistry.forEach(function (element) {
        canvas.removeMarker(element.id, 'bpmn-completed');
        canvas.removeMarker(element.id, 'bpmn-active');
    });

    if (activeIds) {
        activeIds.forEach(function (id) {
            if (elementRegistry.get(id)) {
                canvas.addMarker(id, 'bpmn-active');
            }
        });
    }
},
```

**Step 2: Add the `highlightExecutionPath` method**

Add this new method after the `highlight` method. It:
1. Receives an ordered array of activity IDs (sorted by `CreatedAt`)
2. Builds a map of all sequence flows keyed by `sourceId→targetId`
3. For each activity, searches backwards in the list for a predecessor connected by a sequence flow
4. Counts traversals per flow
5. Draws semi-transparent SVG paths on a custom canvas layer using the flow's waypoints
6. Adds SVG arrowhead marker definition for direction

```javascript
highlightExecutionPath: function (orderedActivityIds) {
    if (!this._viewer) return;

    var canvas = this._viewer.get('canvas');
    var elementRegistry = this._viewer.get('elementRegistry');

    // Get or create the execution-path layer (above default layer)
    var layer = canvas.getLayer('executionPath', 1);

    // Clear previous overlays
    while (layer.firstChild) {
        layer.removeChild(layer.firstChild);
    }

    if (!orderedActivityIds || orderedActivityIds.length < 2) return;

    // Build map: "sourceId→targetId" -> element
    var flowMap = {};
    elementRegistry.forEach(function (el) {
        if (el.type === 'bpmn:SequenceFlow' && el.businessObject.sourceRef && el.businessObject.targetRef) {
            var key = el.businessObject.sourceRef.id + '→' + el.businessObject.targetRef.id;
            flowMap[key] = el;
        }
    });

    // Count traversals per sequence flow
    var flowCounts = {};
    for (var i = 1; i < orderedActivityIds.length; i++) {
        var target = orderedActivityIds[i];
        for (var j = i - 1; j >= 0; j--) {
            var source = orderedActivityIds[j];
            var key = source + '→' + target;
            if (flowMap[key]) {
                var flowId = flowMap[key].id;
                flowCounts[flowId] = (flowCounts[flowId] || 0) + 1;
                break;
            }
        }
    }

    // Add arrowhead marker definition
    var svgRoot = canvas._svg || layer.ownerSVGElement;
    var defs = svgRoot.querySelector('defs');
    if (!defs) {
        defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
        svgRoot.insertBefore(defs, svgRoot.firstChild);
    }

    // Remove old marker if present, then add fresh
    var oldMarker = defs.querySelector('#execution-arrow');
    if (oldMarker) oldMarker.remove();

    var marker = document.createElementNS('http://www.w3.org/2000/svg', 'marker');
    marker.setAttribute('id', 'execution-arrow');
    marker.setAttribute('viewBox', '0 0 10 10');
    marker.setAttribute('refX', '10');
    marker.setAttribute('refY', '5');
    marker.setAttribute('markerWidth', '8');
    marker.setAttribute('markerHeight', '8');
    marker.setAttribute('orient', 'auto');
    var arrowPath = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    arrowPath.setAttribute('d', 'M 0 0 L 10 5 L 0 10 Z');
    arrowPath.setAttribute('fill', 'rgba(59, 130, 246, 0.6)');
    marker.appendChild(arrowPath);
    defs.appendChild(marker);

    // Draw overlay paths
    var flowIds = Object.keys(flowCounts);
    for (var f = 0; f < flowIds.length; f++) {
        var fId = flowIds[f];
        var count = flowCounts[fId];
        var element = elementRegistry.get(fId);
        if (!element || !element.waypoints || element.waypoints.length < 2) continue;

        var d = element.waypoints.map(function (wp, idx) {
            return (idx === 0 ? 'M' : 'L') + wp.x + ',' + wp.y;
        }).join(' ');

        var opacity = Math.min(0.4 + (count - 1) * 0.15, 0.85);

        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', d);
        path.setAttribute('stroke', 'rgba(59, 130, 246, ' + opacity + ')');
        path.setAttribute('stroke-width', '4');
        path.setAttribute('fill', 'none');
        path.setAttribute('stroke-linecap', 'round');
        path.setAttribute('stroke-linejoin', 'round');
        path.setAttribute('marker-end', 'url(#execution-arrow)');

        layer.appendChild(path);
    }
},
```

**Step 3: Verify build**

Run: `dotnet build src/Fleans/Fleans.Web/Fleans.Web.csproj`
Expected: Build succeeded (JS is static content, just confirm no copy errors)

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js
git commit -m "feat: add execution path overlay arrows in bpmnViewer"
```

---

### Task 3: Update ProcessInstance.razor — compute transitions, call JS

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

**Step 1: Update `RenderDiagram` to call new JS methods**

In `RenderDiagram()` (line 361–375), change the `highlight` call to only pass `ActiveActivityIds` (no more `CompletedActivityIds`). Add a call to `highlightExecutionPath` with the ordered activity IDs.

Replace the body of `RenderDiagram`:

```csharp
private async Task RenderDiagram()
{
    if (bpmnXml == null || snapshot == null) return;

    await JS.InvokeVoidAsync("bpmnViewer.init", "bpmn-canvas", bpmnXml);
    await JS.InvokeVoidAsync("bpmnViewer.highlight", snapshot.ActiveActivityIds);

    var orderedActivityIds = snapshot.CompletedActivities
        .Concat(snapshot.ActiveActivities)
        .OrderBy(a => a.CreatedAt)
        .Select(a => a.ActivityId)
        .ToArray();
    await JS.InvokeVoidAsync("bpmnViewer.highlightExecutionPath", (object)orderedActivityIds);

    dotNetRef = DotNetObjectReference.Create(this);
    await JS.InvokeVoidAsync("bpmnViewer.registerClickHandler", dotNetRef);

    var viewportHeight = await JS.InvokeAsync<int>("dragResize.getViewportHeight");
    var maxHeight = Math.Max(viewportHeight - 200, 400);
    await JS.InvokeVoidAsync("dragResize.initVertical", "vertical-drag-handle", "bpmn-canvas", 200, maxHeight, dotNetRef, "OnCanvasResized");
}
```

**Step 2: Update `Refresh` to call new JS methods**

In `Refresh()` (line 377–405), update the `highlight` call the same way and add `highlightExecutionPath`.

Replace the highlight section inside the `if (snapshot != null)` block:

```csharp
if (snapshot != null)
{
    await JS.InvokeVoidAsync("bpmnViewer.highlight", snapshot.ActiveActivityIds);

    var orderedActivityIds = snapshot.CompletedActivities
        .Concat(snapshot.ActiveActivities)
        .OrderBy(a => a.CreatedAt)
        .Select(a => a.ActivityId)
        .ToArray();
    await JS.InvokeVoidAsync("bpmnViewer.highlightExecutionPath", (object)orderedActivityIds);

    if (selectedActivityId != null)
    {
        await JS.InvokeVoidAsync("bpmnViewer.selectElement", selectedActivityId);
    }
}
```

**Step 3: Build and verify**

Run: `dotnet build src/Fleans/Fleans.Web/Fleans.Web.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor
git commit -m "feat: compute execution path transitions and render overlay arrows"
```

---

### Task 4: Manual verification

**Step 1: Run the full stack**

Run: `dotnet run --project src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj`

**Step 2: Verify visually**

1. Open the web UI, navigate to a process instance that has completed activities
2. Confirm: completed activities have NO green fill (default color)
3. Confirm: active/running activities have a pulsing blue border
4. Confirm: semi-transparent blue arrows appear on the sequence flows that were traversed
5. Confirm: clicking elements still shows the amber selected highlight
6. Confirm: the Refresh button re-renders the execution path correctly

**Step 3: Final commit (if any fixes needed)**
