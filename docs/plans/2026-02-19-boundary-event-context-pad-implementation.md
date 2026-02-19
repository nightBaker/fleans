# Boundary Event Context Pad Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add context pad entries to boundarable activities (tasks, call activities) so users can attach timer, message, and error boundary events directly from the editor canvas.

**Architecture:** A custom bpmn-js `ContextPadProvider` module defined in a new JS file, registered via `additionalModules` on the modeler. No Blazor/C# changes needed.

**Tech Stack:** Vanilla JavaScript, bpmn-js v17.11.1 (CDN), Blazor Server

**Known limitation:** Error boundary events will display as "Boundary Event" in the properties panel — error-specific property editing (error code/message) is out of scope.

---

### Task 1: Create the boundary event context pad provider module

**Files:**
- Create: `src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js`

**Step 1: Write the provider module**

Create `src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js` with this content:

```javascript
(function () {
    var BOUNDARABLE_TYPES = [
        'bpmn:Task',
        'bpmn:UserTask',
        'bpmn:ServiceTask',
        'bpmn:ScriptTask',
        'bpmn:CallActivity',
        'bpmn:SubProcess'
    ];

    function BoundaryEventContextPadProvider(contextPad, modeling, elementFactory, bpmnFactory, selection) {
        contextPad.registerProvider(600, this);

        this._modeling = modeling;
        this._elementFactory = elementFactory;
        this._bpmnFactory = bpmnFactory;
        this._selection = selection;
    }

    BoundaryEventContextPadProvider.$inject = [
        'contextPad', 'modeling', 'elementFactory', 'bpmnFactory', 'selection'
    ];

    BoundaryEventContextPadProvider.prototype.getContextPadEntries = function (element) {
        var bo = element.businessObject;
        if (BOUNDARABLE_TYPES.indexOf(bo.$type) === -1) return {};

        var self = this;

        return {
            'attach-boundary-timer': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-timer',
                title: 'Attach Timer Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:TimerEventDefinition');
                    }
                }
            },
            'attach-boundary-message': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-message',
                title: 'Attach Message Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:MessageEventDefinition');
                    }
                }
            },
            'attach-boundary-error': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-error',
                title: 'Attach Error Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:ErrorEventDefinition');
                    }
                }
            }
        };
    };

    BoundaryEventContextPadProvider.prototype._createBoundaryEvent = function (host, eventDefinitionType) {
        var bpmnFactory = this._bpmnFactory;
        var elementFactory = this._elementFactory;
        var modeling = this._modeling;
        var selection = this._selection;

        var eventDefinition = bpmnFactory.create(eventDefinitionType);

        var boundaryEvent = bpmnFactory.create('bpmn:BoundaryEvent', {
            attachedToRef: host.businessObject,
            cancelActivity: eventDefinitionType !== 'bpmn:ErrorEventDefinition',
            eventDefinitions: [eventDefinition]
        });

        eventDefinition.$parent = boundaryEvent;

        var shape = elementFactory.createShape({
            type: 'bpmn:BoundaryEvent',
            businessObject: boundaryEvent
        });

        var position = {
            x: host.x + host.width,
            y: host.y + host.height
        };

        var created = modeling.createShape(shape, position, host, { attach: true });

        selection.select(created);
    };

    window.boundaryEventContextPadModule = {
        __init__: ['boundaryEventContextPadProvider'],
        boundaryEventContextPadProvider: ['type', BoundaryEventContextPadProvider]
    };
})();
```

**Step 2: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js
git commit -m "feat: add boundary event context pad provider module"
```

---

### Task 2: Register the module in the modeler

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js:15-21`

**Step 1: Add `additionalModules` to the modeler constructor**

In `bpmnEditor.js`, replace lines 15-21:

```javascript
        this._modeler = new BpmnModeler({
            container: container,
            keyboard: { bindTo: container },
            moddleExtensions: window.fleansModdleExtension
                ? { fleans: window.fleansModdleExtension }
                : {}
        });
```

With:

```javascript
        this._modeler = new BpmnModeler({
            container: container,
            keyboard: { bindTo: container },
            moddleExtensions: window.fleansModdleExtension
                ? { fleans: window.fleansModdleExtension }
                : {},
            additionalModules: window.boundaryEventContextPadModule
                ? [window.boundaryEventContextPadModule]
                : []
        });
```

**Step 2: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/bpmnEditor.js
git commit -m "feat: register boundary event context pad module in modeler"
```

---

### Task 3: Add the script tag to the layout

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/App.razor:31`

**Step 1: Add script tag before bpmnEditor.js**

In `App.razor`, add the new script tag after line 30 (`fleansModdleExtension.js`) and before line 31 (`bpmnEditor.js`). The script must load after the modeler CDN but before `bpmnEditor.js` which uses it.

After the change, lines 30-32 should be:

```html
<script src="js/fleansModdleExtension.js"></script>
<script src="js/boundaryEventContextPad.js"></script>
<script src="js/bpmnEditor.js"></script>
```

**Step 2: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/App.razor
git commit -m "feat: load boundary event context pad script in layout"
```

---

### Task 4: Manual verification

**Step 1: Run the application**

```bash
dotnet run --project src/Fleans/Fleans.Aspire
```

**Step 2: Verify in browser**

1. Navigate to `/editor`
2. Add a Task element to the canvas (drag from palette)
3. Click the Task — context pad should show 3 new icons (timer, message, error boundary events) in addition to the existing append/connect/delete icons
4. Click the timer boundary icon — a boundary timer event should appear attached to the task
5. Click the new boundary event — the properties panel should show "Boundary Timer Event" with timer fields
6. Repeat for message and error boundary events
7. Click Ctrl+Z — boundary event should be removed (undo works)
8. Export XML (Download XML button) — verify boundary events are in the BPMN XML with correct `attachedToRef`
9. Re-import the downloaded XML — boundary events should render correctly

**Step 3: Verify with Call Activity**

1. Add a Call Activity to the canvas
2. Click it — same 3 boundary icons should appear
3. Attach a timer boundary event
4. Verify it attaches correctly
