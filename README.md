<p align="center">
  <img src="logo.svg" width="128" height="128" alt="Fleans logo"/>
</p>

# fleans
Workflow engine based on Orleans

## Prerequisites

- dotnet 10

## Running the Application

Use Orleans.Aspire as startup project.
It runs 2 applications :
- Fleans.Api - workflow engine - orleans silo 
- Fleans.Web - blazor application - admin panel for workflow

> **Note:** Aspire is the dev orchestrator. For production deployments (Docker Compose, Kubernetes, bare VM), see the [deployment guide](https://nightbaker.github.io/fleans/reference/self-hosting/) on the website.

## BPMN coverage

The authoritative BPMN element coverage matrix lives on the docs site:
**https://nightbaker.github.io/fleans/concepts/bpmn-support/**

See the website for: status (✅ / ⚠️ / 🚧 / ❌) per element variant, source-line pins
to `BpmnConverter.cs`, and the manual-test fixture exercising each element end-to-end.


## Architecture

### Domain

A **workflow** is a stateless structure that defines a business process, constructed from BPMN.

* A **workflow instance** contains the state of a running version of the business process.
* **Domain activities** contain stateless logic that determines how an activity changes the state of the workflow instance. However, these activities do not execute any business logic of the process.
* **Activity instances** can publish events to an Orleans stream. External or internal grains can subscribe to these events, execute the business logic, and then complete the current activity instance.

### Application

Application layer contains staff related to stream event handlers.


