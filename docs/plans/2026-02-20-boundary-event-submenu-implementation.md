# Boundary Event Context Pad Submenu Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace 3 separate boundary event context pad buttons with a single button that opens a popup submenu.

**Architecture:** Use bpmn-js's built-in `popupMenu` service to register a custom popup menu provider. A single context pad entry opens the menu on click.

**Tech Stack:** bpmn-js v17 (PopupMenu, ContextPad), vanilla JS

---

### Task 1: Refactor boundaryEventContextPad.js to use PopupMenu

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js`

**Step 1: Add `popupMenu` to the dependency injection**

Change the constructor and `$inject` to include `popupMenu`:

```javascript
function BoundaryEventContextPadProvider(contextPad, modeling, elementFactory, bpmnFactory, selection, popupMenu) {
    contextPad.registerProvider(600, this);

    this._modeling = modeling;
    this._elementFactory = elementFactory;
    this._bpmnFactory = bpmnFactory;
    this._selection = selection;
    this._popupMenu = popupMenu;
}

BoundaryEventContextPadProvider.$inject = [
    'contextPad', 'modeling', 'elementFactory', 'bpmnFactory', 'selection', 'popupMenu'
];
```

**Step 2: Register a PopupMenu provider for `'boundary-event'`**

Add this after the constructor:

```javascript
BoundaryEventContextPadProvider.prototype._registerPopupMenuProvider = function () {
    var self = this;

    this._popupMenu.registerProvider('boundary-event', {
        getPopupMenuEntries: function () {
            return {
                'attach-boundary-timer': {
                    label: 'Timer Boundary Event',
                    className: 'bpmn-icon-intermediate-event-catch-timer',
                    action: function () {
                        self._createBoundaryEvent(self._currentElement, 'bpmn:TimerEventDefinition');
                    }
                },
                'attach-boundary-message': {
                    label: 'Message Boundary Event',
                    className: 'bpmn-icon-intermediate-event-catch-message',
                    action: function () {
                        self._createBoundaryEvent(self._currentElement, 'bpmn:MessageEventDefinition');
                    }
                },
                'attach-boundary-error': {
                    label: 'Error Boundary Event',
                    className: 'bpmn-icon-intermediate-event-catch-error',
                    action: function () {
                        self._createBoundaryEvent(self._currentElement, 'bpmn:ErrorEventDefinition');
                    }
                }
            };
        }
    });
};
```

Call `this._registerPopupMenuProvider()` at the end of the constructor.

**Step 3: Replace 3 context pad entries with 1 that opens popup menu**

Replace `getContextPadEntries` to return a single entry:

```javascript
BoundaryEventContextPadProvider.prototype.getContextPadEntries = function (element) {
    var bo = element.businessObject;
    if (BOUNDARABLE_TYPES.indexOf(bo.$type) === -1) return {};

    var self = this;

    return {
        'attach-boundary-event': {
            group: 'attach',
            className: 'bpmn-icon-intermediate-event-none',
            title: 'Attach Boundary Event',
            action: {
                click: function (event, element) {
                    self._currentElement = element;

                    var position = {
                        x: event.x,
                        y: event.y
                    };

                    self._popupMenu.open(element, 'boundary-event', position);
                }
            }
        }
    };
};
```

**Step 4: Verify the `_createBoundaryEvent` method is unchanged**

The existing `_createBoundaryEvent` prototype method stays exactly as-is — no changes needed.

**Step 5: Run the app and verify**

Run: `dotnet run --project src/Fleans/Fleans.Aspire/`

Manual verification:
1. Open BPMN editor
2. Add a ScriptTask or other boundaryable activity
3. Click on the activity — context pad should show ONE boundary event button (circle icon)
4. Click it — popup menu should appear with 3 options (Timer, Message, Error)
5. Select Timer — a timer boundary event should be attached to the activity
6. Repeat for Message and Error

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js
git commit -m "feat: consolidate boundary event buttons into popup submenu"
```
