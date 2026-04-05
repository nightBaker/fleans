# UserTask Properties Panel — Design

**Issue:** [#186](https://github.com/nightBaker/fleans/issues/186)
**Date:** 2026-04-05

## Problem

The BPMN editor's right properties panel has no UI for UserTask properties. The domain model (`UserTask.cs`) and BPMN converter already support Assignee, CandidateGroups, CandidateUsers, and ExpectedOutputVariables, but they can't be set from the editor.

## Design

When a `bpmn:UserTask` is selected in the editor, the right panel shows 4 property sections:

### Properties

1. **Assignee** — single `FluentTextField`, free-text. Maps to `camunda:assignee` attribute.

2. **Candidate Groups** — tag/chip input. Type a group name, press Enter to add as a chip. Click X to remove. Persisted as comma-separated `camunda:candidateGroups` attribute.

3. **Candidate Users** — tag/chip input, same pattern. Persisted as comma-separated `camunda:candidateUsers` attribute.

4. **Expected Output Variables** — tag/chip input with autocomplete. Typing suggests variable names extracted from other ScriptTasks in the workflow. Persisted as `<fleans:expectedOutputs><fleans:output name="..." /></fleans:expectedOutputs>` extension elements.

### Tag/Chip Input Component

Fluent UI Blazor doesn't have a built-in tag input. Build a minimal reusable component:
- `FluentTextField` for text entry
- List of `FluentBadge` chips with dismiss (X) buttons
- On Enter: add current text as chip, clear field
- On X click: remove chip
- Optional `Suggestions` parameter: when set, show filtered dropdown as user types (used for ExpectedOutputVariables autocomplete)
- Reused for CandidateGroups, CandidateUsers, and ExpectedOutputVariables

### Autocomplete for Expected Output Variables

The JS layer extracts variable names from the BPMN diagram (ScriptTask scripts, existing variable references) and passes them to Blazor as a `string[]` on `BpmnElementData`. The tag input shows a filtered dropdown of suggestions as the user types.

## Changes by Layer

| Layer | File | Change |
|-------|------|--------|
| Blazor | `ElementPropertiesPanel.razor` | Add UserTask section after Signal Events (~line 184) |
| Blazor | New `TagInput.razor` component | Reusable tag/chip input with optional autocomplete |
| JS | `bpmnEditor.js` `_extractElementData()` | Extract `assignee`, `candidateGroups`, `candidateUsers`, `expectedOutputVariables` from Camunda attrs + Fleans extensions |
| JS | `bpmnEditor.js` | Add `updateUserTaskProperty()` for persisting changes back to BPMN model |
| JS | `fleansModdleExtension.js` | Add `ExpectedOutputs` and `Output` type definitions for Fleans extension elements |
| C# model | `BpmnElementData` class | Add `Assignee`, `CandidateGroups`, `CandidateUsers`, `ExpectedOutputVariables`, `AvailableVariables` properties |

## BPMN XML Format

```xml
<bpmn:userTask id="task1" name="Review"
    camunda:assignee="user1"
    camunda:candidateGroups="managers,reviewers"
    camunda:candidateUsers="alice,bob">
  <bpmn:extensionElements>
    <fleans:expectedOutputs>
      <fleans:output name="approved" />
      <fleans:output name="comment" />
    </fleans:expectedOutputs>
  </bpmn:extensionElements>
</bpmn:userTask>
```

## Existing Pattern Reference

Follow the same pattern as ScriptTask (scriptFormat + script fields), CallActivity (calledElement + mappings), and Message Events (messageName + correlationKey) — all already in `ElementPropertiesPanel.razor`.
