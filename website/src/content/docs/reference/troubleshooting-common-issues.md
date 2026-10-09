---
title: Common Issues
description: Known issues, workarounds, and behavior gaps in the current Fleans release.
---

This page documents known bugs and behavior gaps. For the full element-level coverage status, see [BPMN Support](/fleans/concepts/bpmn-support/).

## Boundary events on intermediate-catch don't fire

**Affects:** intermediate timer, message, and signal catch events with a boundary event attached.

**Symptom:** the boundary event never fires. The host activity blocks until something else (cancel, completion, or timeout from a higher scope) interrupts it.

**Root cause:** boundary subscriptions are not registered when the host is an `IntermediateCatchEvent`. Boundaries on Tasks, Subprocesses, and Call Activities are unaffected.

**Workaround:** wrap the intermediate-catch in a single-activity subprocess and attach the boundary event to the subprocess instead.

**Fixtures reproducing this issue:**

- [`tests/manual/08-timer-events`](https://github.com/nightBaker/fleans/tree/main/tests/manual/08-timer-events)
- [`tests/manual/09-message-events`](https://github.com/nightBaker/fleans/tree/main/tests/manual/09-message-events)
- [`tests/manual/10-signal-events`](https://github.com/nightBaker/fleans/tree/main/tests/manual/10-signal-events)

## Child-process errors don't propagate to parent error boundary on CallActivity

**Affects:** call activities with an error boundary event, where the called child process throws an unhandled error.

**Symptom:** the CallActivity stays in `Running` state indefinitely. The error boundary event on the parent does not fire.

**Root cause:** error events emitted from the child workflow don't trigger the parent's CallActivity error-boundary subscription.

**Workaround:** handle the error inside the child process and signal the parent via a normal end event with a discriminator variable. The parent reads the variable and gateway-routes accordingly.

**Fixtures reproducing this issue:**

- [`tests/manual/11-error-boundary`](https://github.com/nightBaker/fleans/tree/main/tests/manual/11-error-boundary)

## Instance shows *Failed* (or used to sit in *Running* with no active activities)

**Affects:** any workflow where an activity fails and nothing handles the error.

**Symptom:** the instance's status is **Failed** (`isFailed: true` on `GET /Workflow/instances/{id}/state`) and one activity in `completedActivities` carries an `errorState`. On releases before the fix for [#762](https://github.com/nightBaker/fleans/issues/762) the same situation showed up as an instance stuck in *Running* with `activeActivityIds: []`, neither completed nor failed.

**Root cause:** the failing activity had no error boundary event and no enclosing error event sub-process, so the error ended the last live branch. A frequent trigger is a script task whose script DynamicExpresso cannot parse — e.g. `_context.items = new[] { 1, 2, 3 }` (implicitly-typed array literals are unsupported).

**Fix:** read the failing activity's `errorState` (also logged at `Error` level with EventId 1120). Fix the cause — for the array example use `new List<object> { 1, 2, 3 }` — or attach an error boundary / error event sub-process if the workflow should recover.

**Fixtures reproducing this:**

- [`tests/manual/69-unhandled-failure-fails-instance`](https://github.com/nightBaker/fleans/tree/main/tests/manual/69-unhandled-failure-fails-instance)

## Related

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element-level status table.
- [Error Handling](/fleans/guides/error-handling/) — supported error handling patterns.
