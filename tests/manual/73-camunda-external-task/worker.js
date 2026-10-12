// Unmodified-style Camunda 7 worker (camunda-external-task-client-js). Only baseUrl points at Fleans.
// Usage: npm i camunda-external-task-client-js@3 && MODE=complete|bpmnError|failure node worker.js
const { Client, logger, Variables } = require("camunda-external-task-client-js");
const mode = process.env.MODE || "complete";
const client = new Client({
  baseUrl: process.env.BASE_URL || "http://localhost:8080/engine-rest",
  workerId: "manual-js-worker",
  asyncResponseTimeout: 10000,
  use: logger,
});
client.subscribe("charge-card", async ({ task, taskService }) => {
  console.log("amount =", task.variables.get("amount"), "amountToCharge =", task.variables.get("amountToCharge"));
  if (mode === "bpmnError") return taskService.handleBpmnError(task, "PAYMENT_DECLINED", "insufficient funds");
  if (mode === "failure") return taskService.handleFailure(task, { errorMessage: "card service down", errorDetails: "HTTP 503", retries: 0, retryTimeout: 0 });
  const vars = new Variables();
  vars.set("charged", true);
  vars.set("receipt", { id: "R-1", total: 42.5 });
  await taskService.complete(task, vars);
});
