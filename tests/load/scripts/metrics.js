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

import { Rate, Trend } from 'k6/metrics';

// Duration of the POST /Execution/start HTTP call, in milliseconds.
// Each dependency script must call: workflowStartDuration.add(res.timings.duration)
// at its POST /Execution/start call site.
//
// WARNING: k6 silently skips thresholds for metrics that are never emitted.
// If a script does not call .add(), the 'workflow_start_duration' threshold in
// mixed.js will be silently ignored rather than failed. Ensure all three
// dependency scripts instrument this metric.
export const workflowStartDuration = new Trend('workflow_start_duration');

export const pollUntilCatchDuration = new Trend('poll_until_catch_duration');
export const messageAcceptDuration  = new Trend('message_accept_duration');
export const messageRetryAttempts   = new Trend('message_retry_attempts');
export const pollStallsRate         = new Rate('poll_stalls');
export const correlationMissRate    = new Rate('correlation_miss');

// long-diverse.js — per-stage wall-clock (ms) and outcome rates for the long process.
export const ldStageToApproval    = new Trend('ld_stage_to_approval');     // start → waitApproval active
export const ldStageToReview      = new Trend('ld_stage_to_review');       // approval sent → manualReview active
export const ldStageToConfirm     = new Trend('ld_stage_to_confirm');      // review completed → waitConfirm active
export const ldStageToCompleted   = new Trend('ld_stage_to_completed');    // confirm sent → isCompleted
export const ldEndToEnd           = new Trend('ld_end_to_end');            // start → isCompleted (client-observed)
export const ldEngineDuration     = new Trend('ld_engine_duration');       // completedAt − createdAt from the state snapshot
export const ldCompletedRate      = new Rate('ld_completed');              // iteration reached isCompleted
export const ldFailedRate         = new Rate('ld_instance_failed');        // engine reported isFailed
export const ldStallRate          = new Rate('ld_poll_stall');             // a stage exceeded its poll budget
export const ldPathMismatchRate   = new Rate('ld_path_mismatch');          // completed, but wrong branches taken
