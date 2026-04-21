// tests/load/scripts/metrics.js
//
// Shared k6 metric instances for the Fleans load test suite.
//
// Import from here — do NOT call new Trend() with the same name in dependency scripts.
// k6 uses a global metric registry keyed by name; defining the same metric in multiple
// modules is an undocumented implementation detail. Using a single shared module
// avoids any ambiguity around duplicate registration across k6 versions.
//
// Required by: linear.js (#239), parallel.js (#240), events.js (#241), mixed.js (#242)

import { Trend } from 'k6/metrics';

// Duration of the POST /Workflow/start HTTP call, in milliseconds.
// Each dependency script must call: workflowStartDuration.add(res.timings.duration)
// at its POST /Workflow/start call site.
//
// WARNING: k6 silently skips thresholds for metrics that are never emitted.
// If a script does not call .add(), the 'workflow_start_duration' threshold in
// mixed.js will be silently ignored rather than failed. Ensure all three
// dependency scripts instrument this metric.
export const workflowStartDuration = new Trend('workflow_start_duration');
