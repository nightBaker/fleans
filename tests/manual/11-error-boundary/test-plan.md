# 11 — Error Boundary Event

## Scenario
A parent process calls a child that throws an exception. An error boundary event on the call activity catches the error and routes to an error-handling path. The parent does NOT fail.

## Prerequisites
- Aspire stack running
- **Deploy `child-that-fails.bpmn` FIRST**

## Steps

### 1. Deploy child process
- Import `child-that-fails.bpmn`, deploy

### 2. Deploy parent process
- Import `error-on-call-activity.bpmn`, deploy

### 3. Start the parent
- Start `error-boundary-test`, open Instance Viewer

### 4. Verify outcome
- [ ] Parent instance status: **Completed** (NOT failed)
- [ ] `errorHandler` in completed activities (error boundary caught the exception)
- [ ] `happyEnd` NOT in completed activities
- [ ] Variables: `errorHandled` = **true**
- [ ] Activities tab: `callFailing` shows error details (code 500, message "Something went wrong")
- [ ] BPMN canvas highlights the error path
