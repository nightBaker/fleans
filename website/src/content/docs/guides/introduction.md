---
title: Introduction
description: What Fleans is and who it's for.
---

Fleans is a BPMN 2.0 workflow engine built on [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/).
It aims to bring Camunda-style workflow orchestration to the .NET ecosystem — without a JVM, without
an external workflow server, and with Orleans' actor model as the execution substrate.

## What is BPMN?

BPMN (Business Process Model and Notation) is the industry-standard visual language for
describing workflows. It defines a small set of symbols — events, activities, gateways,
and flows — that combine into executable process diagrams. If you are new to BPMN,
read **[What is BPMN?](/fleans/concepts/bpmn-overview/)** for a quick primer before
diving into Fleans-specific features.

## Who is this for?

- .NET teams who want BPMN-driven orchestration without running Camunda or Temporal alongside their app.
- Teams already invested in Orleans who need long-running workflows.
- Anyone building approval flows, order processing, or multi-step business logic that benefits from
  a visual workflow model.

## What's supported today?

Fleans implements a growing subset of BPMN 2.0. See [BPMN Support](/fleans/concepts/bpmn-support/) for the
current coverage matrix.
