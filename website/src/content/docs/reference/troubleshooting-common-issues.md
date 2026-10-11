---
title: Common Issues
description: Known issues, workarounds, and behavior gaps in the current Fleans release.
---

This page documents known bugs and behavior gaps. For the full element-level coverage status, see [BPMN Support](/fleans/concepts/bpmn-support/).

There are no known issues at the moment. If you hit one, please [open an issue](https://github.com/nightBaker/fleans/issues).

## Instance shows *Failed* (or used to sit in *Running* with no active activities)

**Affects:** any workflow where an activity fails and nothing handles the error.

**Symptom:** the instance's status is **Failed** (`isFailed: true` on `GET /Instances/{id}/state`) and one activity in `completedActivities` carries an `errorState`. On releases before the fix for [#762](https://github.com/nightBaker/fleans/issues/762) the same situation showed up as an instance stuck in *Running* with `activeActivityIds: []`, neither completed nor failed.

**Root cause:** the failing activity had no error boundary event and no enclosing error event sub-process, so the error ended the last live branch. A frequent trigger is a script task whose script DynamicExpresso cannot parse — e.g. `_context.items = new[] { 1, 2, 3 }` (implicitly-typed array literals are unsupported).

**Fix:** read the failing activity's `errorState` (also logged at `Error` level with EventId 1120). Fix the cause — for the array example use `new List<object> { 1, 2, 3 }` — or attach an error boundary / error event sub-process if the workflow should recover.

**Fixtures reproducing this:**

- [`tests/manual/71-unhandled-failure-fails-instance`](https://github.com/nightBaker/fleans/tree/main/tests/manual/71-unhandled-failure-fails-instance)

## Related

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element-level status table.
- [Error Handling](/fleans/guides/error-handling/) — supported error handling patterns.
