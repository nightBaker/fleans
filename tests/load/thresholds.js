// k6 v0.54 cannot import .json files as ES modules (Goja parses them as JavaScript).
// Threshold definitions live here as a JS default export; thresholds.json is kept for
// tooling/IDE reference only.
export default {
  http_req_failed:           ["rate<0.01"],
  http_req_duration:         ["p(95)<2000"],
  workflow_start_duration:   ["p(95)<2000"],
  poll_until_catch_duration: ["p(95)<2500"],
  message_accept_duration:   ["p(95)<2000"],
  poll_stalls:               ["rate<0.01"],
  correlation_miss:          ["rate<0.01"],
};
