# Execution Path Overlay Arrows — Design

**Date:** 2026-02-17

## Problem

The current process instance BPMN viewer colors completed activities green and active activities blue. This approach doesn't convey the execution path — when an activity is processed multiple times (e.g., loops), you can't see from where it was reached or how many times it ran.

## Solution

Replace activity coloring with semi-transparent overlay arrows drawn on top of the BPMN sequence flows that were actually traversed during execution.

### What changes

1. **Remove `bpmn-completed` marker** — completed activities stay default color (no green fill)
2. **Keep `bpmn-active` marker** — add CSS pulsing border animation for currently running activities
3. **Keep `bpmn-selected` marker** — amber highlight unchanged
4. **Add execution path overlay arrows** — semi-transparent blue arrows following the actual sequence flow paths that were traversed

### Data flow

1. **Blazor (ProcessInstance.razor):** Combine `CompletedActivities` + `ActiveActivities`, sort by `CreatedAt` timestamp. For each consecutive pair of activities, emit a transition record `{ sourceActivityId, targetActivityId }`.
2. **JavaScript (bpmnViewer.js):** New `highlightExecutionPath(transitions)` method:
   - For each transition, find the `bpmn:SequenceFlow` element in `elementRegistry` whose `businessObject.sourceRef.id` matches the source and `businessObject.targetRef.id` matches the target
   - Clone the sequence flow's SVG `<path>` element as an overlay with semi-transparent styling
   - For flows traversed multiple times: increase opacity progressively
3. **CSS (app.css):** Overlay arrow styles (`stroke: rgba(59, 130, 246, 0.4)`, `stroke-width: 4px`), pulsing animation for active marker

### Files changed

- `Fleans.Web/wwwroot/js/bpmnViewer.js` — add `highlightExecutionPath()`, update `highlight()` to stop applying `bpmn-completed`
- `Fleans.Web/wwwroot/app.css` — remove `.bpmn-completed`, add pulsing animation to `.bpmn-active`, add overlay styles
- `Fleans.Web/Components/Pages/ProcessInstance.razor` — compute transitions from sorted activities, pass to JS

### Visual spec

- Overlay arrows: `stroke: rgba(59, 130, 246, 0.4)` (blue at 40% opacity), `stroke-width: 4px`, with arrowhead marker
- Active activities: pulsing blue border (keyframe animation)
- Multiple traversals: opacity increases per traversal (0.4 base + 0.15 per extra traversal, capped at 0.85)
