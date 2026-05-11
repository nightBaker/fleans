# Manual Test Plan — #522: EndEvent Variant Coverage

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`

## Test steps

| # | Action | Expected |
|---|--------|----------|
| 1 | Import `terminate-end.bpmn`, click `terminate_end` node | Panel type label: **"Terminate End Event"**; no editable fields |
| 2 | Import `error-end.bpmn`, click `error_end` node | Panel type label: **"Error End Event"**; "Error Code" field shows **"PAY-500"** |
| 3 | Import `signal-end.bpmn`, click `signal_end` node | Panel type label: **"Signal End Event"**; "Signal Name" field shows **"processCompleted"** |
| 4 | Import `message-end.bpmn`, click `message_end` node | Panel type label: **"Message End Event"**; "Message Name" field shows **"orderCompleted"**; **no** Correlation Key field visible |
| 5 | Import any fixture with a Message Intermediate Throw Event (if available) | Correlation Key field is **not** shown (throwing events do not correlate) |
