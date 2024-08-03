# fleans
Workflow engine based on Orleans

## Prerequisites

- dotnet 8

## Running the Application

Use Orleans.Aspire as startup project.
It runs 2 applications :
- Fleans.Api - workflow engine - orleans silo 
- Fleans.Web - blazor application - admin panel for workflow

## Bpmn elements 
For now, next elements are implemented 

| Notation             | Description                                                                 | Implemented |
|----------------------|-----------------------------------------------------------------------------|-------------|
| **Flow Objects**     |                                                                             |             |
| Start Event          | Indicates where a process will start.                                        |     [x]     |
| Intermediate Event   | Indicates something that happens between the start and end events.           |             |
| End Event            | Indicates where a process will end.                                          |     [x]     |
| Message Event        | Represents the sending or receiving of a message.                            |             |
| Timer Event          | Represents a delay or a specific time/date.                                  |             |
| Error Event          | Indicates an error that needs to be handled.                                 |             |
| Conditional Event    | Represents a condition that will cause a process to start or continue.       |             |
| Signal Event         | Represents the sending or receiving of a signal.                             |             |
| Escalation Event     | Used to model situations where escalation is required.                       |             |
| Cancel Event         | Indicates cancellation of a process.                                         |             |
| Compensation Event   | Represents a process that is performed to compensate for an error.           |             |
| Multiple Event       | Indicates that multiple events can occur.                                    |             |
| **Activities**       |                                                                             |             |
| Task                 | A single unit of work.                                                      |             |
| Sub-Process          | A group of tasks that are treated as a single unit.                         |             |
| Call Activity        | A type of sub-process that calls another process.                            |             |
| Transaction          | A set of activities that are handled as a single unit.                      |             |
| Event Sub-Process    | A sub-process that is triggered by an event.                                 |             |
| **Gateways**         |                                                                             |             |
| Exclusive Gateway    | Indicates a decision point where only one path can be taken.                 |    [x]      |
| Inclusive Gateway    | Indicates a decision point where one or more paths can be taken.             |             |
| Parallel Gateway     | Indicates that all paths are taken in parallel.                              |             |
| Complex Gateway      | Indicates a complex decision point with conditions.                          |             |
| Event-Based Gateway  | Indicates that the process flow is determined by an event.                   |             |
| **Connecting Objects**|                                                                            |             |
| Sequence Flow        | Shows the order of activities.                                              |      [x]     |
| Message Flow         | Shows the flow of messages between different participants.                  |             |
| Association          | Links artifacts and text to flow objects.                                    |             |
| Data Association     | Links data objects and data stores to activities.                            |             |
| **Swimlanes**        |                                                                             |             |
| Pool                 | Represents a participant in the process.                                     |             |
| Lane                 | Sub-partitions within a pool, used to organize activities.                   |             |
| **Artifacts**        |                                                                             |             |
| Data Object          | Represents data that is required or produced by activities.                  |             |
| Data Store           | Represents a place where data is stored.                                     |             |
| Group                | Used to group elements for documentation or analysis purposes.               |             |
| Annotation           | Provides additional information about a process.                             |             |


## Architecture

### Domain

Workflow is stateless structure definition of business process which is constructred from bpmn.  
Worfklow instance contains state of running version of business process.  
Domain activites contain stateless logic how activity changes state of workflow instance.  
However, activites do not execute any business logic of process.
Activity instance can publish event to orleans stream, so external (or internal) grains can subscribe and execute business logic and then complete current activity instance.

### Application

Application layer contains staff related to stream event handlers.


