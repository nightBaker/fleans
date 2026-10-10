---
title: Common Issues
description: Known issues, workarounds, and behavior gaps in the current Fleans release.
---

This page documents known bugs and behavior gaps. For the full element-level coverage status, see [BPMN Support](/fleans/concepts/bpmn-support/).

<figure class="arch-diagram" style="--arch-ratio: 720 / 546">
  <iframe data-arch-src="/fleans/diagrams/troubleshooting-triage.html" src="/fleans/diagrams/troubleshooting-triage.html?embed=1" title="Troubleshooting triage" loading="lazy"></iframe>
  <figcaption>Triage a stuck instance: match its symptom to the known gap, see the root cause, and apply the documented workaround (anything else: check BPMN Support). <a href="/fleans/diagrams/troubleshooting-triage.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

## Timer boundary on an intermediate catch event doesn't fire

**Affects:** a timer boundary event attached to an `IntermediateCatchEvent` host (message and signal boundaries on the same host work).

**Symptom:** the timer boundary never fires. The host catch event blocks until something else (its own trigger, or cancellation from a higher scope) completes it.

**Workaround:** wrap the intermediate-catch in a single-activity subprocess and attach the timer boundary to the subprocess instead.

**Fixtures reproducing this issue:**

- [`tests/manual/08-timer-events`](https://github.com/nightBaker/fleans/tree/main/tests/manual/08-timer-events)

Tracked in [#759](https://github.com/nightBaker/fleans/issues/759).

## Related

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element-level status table.
- [Error Handling](/fleans/guides/error-handling/) — supported error handling patterns.
