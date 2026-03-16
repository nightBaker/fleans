# 18 — User Task

## Scenario
A workflow with a script task that sets variables, followed by a user task with Camunda-style assignment metadata (assignee, candidate groups, candidate users) and expected output variables, followed by a post-processing script task. Verifies the full user task lifecycle: task appears in registry, claim/unclaim, complete with required output variables, and workflow continuation after completion.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy the workflow
- Open the Workflows page
- Click "Create New", import `user-task-approval.bpmn`
- Click Deploy, confirm

### 2. Start an instance
- Click "Start" on `user-task-approval`
- Navigate to the Instance Viewer

### 3. Verify user task is active
- [ ] Instance status: **Running**
- [ ] Activities tab: 2 completed activities (start, prepare)
- [ ] Activities tab: 1 active activity (review, type: UserTask)
- [ ] Variables tab: `requestor` = **"alice"**, `amount` = **500**

### 4. Query pending tasks via API
```bash
curl https://localhost:7140/Workflow/tasks
```
- [ ] Response contains a task with `activityId` = **"review"**
- [ ] `assignee` = **"john"**
- [ ] `candidateGroups` contains **"managers"** and **"leads"**
- [ ] `candidateUsers` contains **"john"** and **"bob"**
- [ ] `taskState` = **"Created"**

### 5. Query single task
```bash
curl https://localhost:7140/Workflow/tasks/{activityInstanceId}
```
- [ ] Returns the task details matching step 4

### 6. Attempt claim by unauthorized user (should fail)
```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/claim \
  -H "Content-Type: application/json" \
  -d '{"UserId": "charlie"}'
```
- [ ] Returns **409 Conflict** (charlie is not in assignee or candidateUsers)

### 7. Claim the task
```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/claim \
  -H "Content-Type: application/json" \
  -d '{"UserId": "john"}'
```
- [ ] Returns **200 OK**

### 8. Attempt complete without required variables (should fail)
```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId": "john", "Variables": {}}'
```
- [ ] Returns **409 Conflict** (missing required output variables: approved, reviewComment)

### 9. Complete the task with required variables
```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId": "john", "Variables": {"approved": true, "reviewComment": "Looks good"}}'
```
- [ ] Returns **200 OK**

### 10. Verify workflow completed
- [ ] Instance status: **Completed**
- [ ] Activities tab: 5 completed activities (start, prepare, review, postReview, end)
- [ ] Variables tab: `approved` = **true**, `reviewComment` = **"Looks good"**, `processed` = **true**
- [ ] No active activities remain

### 11. Verify task unregistered from registry
```bash
curl https://localhost:7140/Workflow/tasks/{activityInstanceId}
```
- [ ] Returns **404 Not Found**
