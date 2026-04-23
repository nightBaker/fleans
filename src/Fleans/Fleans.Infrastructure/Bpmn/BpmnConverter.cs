using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging;

namespace Fleans.Infrastructure.Bpmn;

public partial class BpmnConverter : IBpmnConverter
{
    private const int MaxConversionDepth = 10;
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string BpmndiNamespace = "http://www.omg.org/spec/BPMN/20100524/DI";
    private const string ZeebeNamespace = "http://camunda.org/schema/zeebe/1.0";
    private const string FleansNamespace = "http://fleans.io/schema/bpmn/fleans";
    private const string CamundaNamespace = "http://camunda.org/schema/1.0/bpmn";
    private static readonly XNamespace Bpmn = BpmnNamespace;
    private static readonly XNamespace Bpmndi = BpmndiNamespace;
    private static readonly XNamespace Zeebe = ZeebeNamespace;
    private static readonly XNamespace Fleans = FleansNamespace;
    private static readonly XNamespace Camunda = CamundaNamespace;

    private readonly ILogger<BpmnConverter> _logger;

    public BpmnConverter(ILogger<BpmnConverter> logger)
    {
        _logger = logger;
    }

    public async Task<WorkflowDefinition> ConvertFromXmlAsync(Stream bpmnXmlStream)
    {
        using var reader = new StreamReader(bpmnXmlStream);
        var xml = await reader.ReadToEndAsync();
        return ConvertFromXml(xml);
    }

    private WorkflowDefinition ConvertFromXml(string bpmnXml)
    {
        var doc = XDocument.Parse(bpmnXml);
        var process = doc.Descendants(Bpmn + "process").FirstOrDefault()
            ?? throw new InvalidOperationException("BPMN file must contain a process element");

        var workflowId = process.Attribute("id")?.Value
            ?? throw new InvalidOperationException("Process must have an id attribute");

        var activities = new List<Activity>();
        var sequenceFlows = new List<SequenceFlow>();
        var activityMap = new Dictionary<string, Activity>();
        var defaultFlowIds = new HashSet<string>();

        // Parse message definitions at <definitions> level
        var messages = ParseMessages(doc);

        // Parse signal definitions at <definitions> level
        var signals = ParseSignals(doc);

        // Parse escalation definitions at <definitions> level
        var escalations = ParseEscalations(doc);

        // Parse activities
        ParseActivities(process, activities, activityMap, defaultFlowIds);

        // Parse sequence flows
        ParseSequenceFlows(process, sequenceFlows, activityMap, defaultFlowIds);

        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = sequenceFlows,
            Messages = messages,
            Signals = signals,
            Escalations = escalations
        };

        return workflow;
    }

    private void ParseActivities(XElement scopeElement, List<Activity> activities, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds, bool insideTransaction = false)
    {
        // Pre-parse compensation associations: boundaryEventId -> handlerActivityId.
        // Associations can appear at process level, definitions level, or inside sub-processes.
        var root = scopeElement.Document?.Root;
        var allAssociations = scopeElement.Elements(Bpmn + "association")
            .Concat(root?.Elements(Bpmn + "association") ?? Enumerable.Empty<XElement>())
            .Where(a => a.Attribute("sourceRef") != null && a.Attribute("targetRef") != null)
            .DistinctBy(a => a.Attribute("id")?.Value)
            .GroupBy(a => a.Attribute("sourceRef")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Attribute("targetRef")!.Value);
        var compensationHandlerMap = allAssociations;

        // Parse start events (with optional timer definition)
        foreach (var startEvent in scopeElement.Elements(Bpmn + "startEvent"))
        {
            var id = GetId(startEvent);

            // Check for multiple event definitions
            var eventDefs = CollectEventDefinitions(startEvent, id, "startEvent");
            Activity activity;
            if (eventDefs.Count > 1)
            {
                activity = new MultipleStartEvent(id, eventDefs);
            }
            else
            {
                var timerDef = startEvent.Element(Bpmn + "timerEventDefinition");
                if (timerDef != null)
                {
                    var timerDefinition = ParseTimerDefinition(timerDef);
                    activity = new TimerStartEvent(id, timerDefinition);
                }
                else if (startEvent.Element(Bpmn + "messageEventDefinition") is { } msgDef)
                {
                    var messageRef = msgDef.Attribute("messageRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"startEvent '{id}' messageEventDefinition must have a messageRef attribute");
                    activity = new MessageStartEvent(id, messageRef);
                }
                else if (startEvent.Element(Bpmn + "signalEventDefinition") is { } sigDef)
                {
                    var signalRef = sigDef.Attribute("signalRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"startEvent '{id}' signalEventDefinition must have a signalRef attribute");
                    activity = new SignalStartEvent(id, signalRef);
                }
                else if (startEvent.Element(Bpmn + "errorEventDefinition") is { } errStartDef)
                {
                    var errorRef = errStartDef.Attribute("errorRef")?.Value;
                    var errorCode = ResolveErrorCode(scopeElement, errorRef);
                    activity = new ErrorStartEvent(id, errorCode);
                }
                else
                {
                    activity = new StartEvent(id);
                }
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse intermediate catch events (timer, message, signal, or multiple)
        foreach (var catchEvent in scopeElement.Elements(Bpmn + "intermediateCatchEvent"))
        {
            var id = GetId(catchEvent);

            var eventDefs = CollectEventDefinitions(catchEvent, id, "intermediateCatchEvent");
            Activity activity;
            if (eventDefs.Count > 1)
            {
                activity = new MultipleIntermediateCatchEvent(id, eventDefs);
            }
            else
            {
                var timerDef = catchEvent.Element(Bpmn + "timerEventDefinition");
                var messageDef = catchEvent.Element(Bpmn + "messageEventDefinition");

                if (timerDef != null)
                {
                    var timerDefinition = ParseTimerDefinition(timerDef);
                    activity = new TimerIntermediateCatchEvent(id, timerDefinition);
                }
                else if (messageDef != null)
                {
                    var messageRef = messageDef.Attribute("messageRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"IntermediateCatchEvent '{id}' messageEventDefinition must have a messageRef attribute");
                    activity = new MessageIntermediateCatchEvent(id, messageRef);
                }
                else
                {
                    var signalDef = catchEvent.Element(Bpmn + "signalEventDefinition");
                    if (signalDef != null)
                    {
                        var signalRef = signalDef.Attribute("signalRef")?.Value
                            ?? throw new InvalidOperationException(
                                $"IntermediateCatchEvent '{id}' signalEventDefinition must have a signalRef attribute");
                        activity = new SignalIntermediateCatchEvent(id, signalRef);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"IntermediateCatchEvent '{id}' has an unsupported event definition.");
                    }
                }
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse intermediate throw events (signal, escalation, compensation, or multiple)
        foreach (var throwEvent in scopeElement.Elements(Bpmn + "intermediateThrowEvent"))
        {
            var id = GetId(throwEvent);
            var compensateDef = throwEvent.Element(Bpmn + "compensateEventDefinition");

            var eventDefs = CollectEventDefinitions(throwEvent, id, "intermediateThrowEvent");
            Activity activity;
            if (eventDefs.Count > 1)
            {
                activity = new MultipleIntermediateThrowEvent(id, eventDefs);
            }
            else if (compensateDef != null)
            {
                // Optional: target a specific activity (activityRef attribute)
                var targetActivityRef = compensateDef.Attribute("activityRef")?.Value;
                activity = new CompensationIntermediateThrowEvent(id, targetActivityRef);
            }
            else
            {
                var signalDef = throwEvent.Element(Bpmn + "signalEventDefinition");
                var escalationDef = throwEvent.Element(Bpmn + "escalationEventDefinition");
                if (signalDef != null)
                {
                    var signalRef = signalDef.Attribute("signalRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"IntermediateThrowEvent '{id}' signalEventDefinition must have a signalRef attribute");
                    activity = new SignalIntermediateThrowEvent(id, signalRef);
                }
                else if (escalationDef != null)
                {
                    var escalationRef = escalationDef.Attribute("escalationRef")?.Value;
                    var escalationCode = ResolveEscalationCode(scopeElement, escalationRef)
                        ?? throw new InvalidOperationException(
                            $"IntermediateThrowEvent '{id}' escalationEventDefinition must resolve to an escalation code");
                    activity = new EscalationIntermediateThrowEvent(id, escalationCode);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"IntermediateThrowEvent '{id}' has an unsupported event definition.");
                }
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse end events
        foreach (var endEvent in scopeElement.Elements(Bpmn + "endEvent"))
        {
            var id = GetId(endEvent);

            Activity activity;
            if (endEvent.Element(Bpmn + "compensateEventDefinition") is { } compEndDef)
            {
                activity = new CompensationEndEvent(id, compEndDef.Attribute("activityRef")?.Value);
            }
            else if (endEvent.Element(Bpmn + "escalationEventDefinition") is { } escEndDef)
            {
                var escalationRef = escEndDef.Attribute("escalationRef")?.Value;
                var escalationCode = ResolveEscalationCode(scopeElement, escalationRef)
                    ?? throw new InvalidOperationException(
                        $"endEvent '{id}' escalationEventDefinition must resolve to an escalation code");
                activity = new EscalationEndEvent(id, escalationCode);
            }
            else if (endEvent.Element(Bpmn + "cancelEventDefinition") is not null)
            {
                if (!insideTransaction)
                    throw new InvalidOperationException(
                        $"CancelEndEvent '{id}' is only valid inside a Transaction Sub-Process.");
                activity = new CancelEndEvent(id);
            }
            else
            {
                activity = new EndEvent(id);
            }


            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse tasks
        foreach (var task in scopeElement.Elements(Bpmn + "task"))
        {
            var id = GetId(task);
            Activity activity = new TaskActivity(id);
            activity = TryWrapMultiInstance(task, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse user tasks
        foreach (var userTask in scopeElement.Elements(Bpmn + "userTask"))
        {
            var id = GetId(userTask);
            var assignee = userTask.Attribute(Camunda + "assignee")?.Value;
            var candidateGroups = ParseCommaSeparated(
                userTask.Attribute(Camunda + "candidateGroups")?.Value);
            var candidateUsers = ParseCommaSeparated(
                userTask.Attribute(Camunda + "candidateUsers")?.Value);
            var expectedOutputs = ParseExpectedOutputs(userTask);

            Activity activity = new Domain.Activities.UserTask(id, assignee,
                candidateGroups, candidateUsers, expectedOutputs);
            activity = TryWrapMultiInstance(userTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse service tasks
        foreach (var serviceTask in scopeElement.Elements(Bpmn + "serviceTask"))
        {
            var id = GetId(serviceTask);
            Activity activity = new TaskActivity(id);
            activity = TryWrapMultiInstance(serviceTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse script tasks
        foreach (var scriptTask in scopeElement.Elements(Bpmn + "scriptTask"))
        {
            var id = GetId(scriptTask);
            var scriptFormat = scriptTask.Attribute("scriptFormat")?.Value ?? "csharp";
            ValidateScriptFormat(id, scriptFormat);
            var scriptElement = scriptTask.Element(Bpmn + "script");
            var script = scriptElement?.Value.Trim() ?? "";
            script = ConvertBpmnVariableReferences(script);
            Activity activity = new ScriptTask(id, script, scriptFormat);
            activity = TryWrapMultiInstance(scriptTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse exclusive gateways
        foreach (var gateway in scopeElement.Elements(Bpmn + "exclusiveGateway"))
        {
            var id = GetId(gateway);
            var defaultFlowId = gateway.Attribute("default")?.Value;
            if (defaultFlowId is not null)
                defaultFlowIds.Add(defaultFlowId);

            var activity = new ExclusiveGateway(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse parallel gateways (fork)
        foreach (var gateway in scopeElement.Elements(Bpmn + "parallelGateway"))
        {
            var id = GetId(gateway);
            // Determine if it's a fork or join by checking incoming/outgoing flows
            var incomingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("targetRef")?.Value == id);
            var outgoingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("sourceRef")?.Value == id);

            bool isFork;
            if (outgoingCount > incomingCount)
            {
                isFork = true;
            }
            else if (incomingCount > outgoingCount)
            {
                isFork = false;
            }
            else if (incomingCount <= 1)
            {
                // 1:1 (or 0:0) pass-through — treat as fork for simpler execution
                isFork = true;
            }
            else
            {
                // N:N where N > 1 — mixed parallel gateway (both join and fork)
                // is not supported by the current model which requires fork XOR join.
                throw new InvalidOperationException(
                    $"Parallel gateway '{id}' has {incomingCount} incoming and {outgoingCount} outgoing flows. " +
                    "Mixed parallel gateways (both join and fork) are not supported. " +
                    "Split into separate join and fork gateways.");
            }
            var activity = new ParallelGateway(id, IsFork: isFork);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse inclusive gateways
        foreach (var gateway in scopeElement.Elements(Bpmn + "inclusiveGateway"))
        {
            var id = GetId(gateway);
            var defaultFlowId = gateway.Attribute("default")?.Value;
            if (defaultFlowId is not null)
                defaultFlowIds.Add(defaultFlowId);

            var incomingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("targetRef")?.Value == id);
            var outgoingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("sourceRef")?.Value == id);

            bool isFork;
            if (outgoingCount > incomingCount)
            {
                isFork = true;
            }
            else if (incomingCount > outgoingCount)
            {
                isFork = false;
            }
            else if (incomingCount <= 1)
            {
                isFork = true;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Inclusive gateway '{id}' has {incomingCount} incoming and {outgoingCount} outgoing flows. " +
                    "Mixed inclusive gateways (both join and fork) are not supported. " +
                    "Split into separate join and fork gateways.");
            }

            var activity = new InclusiveGateway(id, IsFork: isFork);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse complex gateways
        foreach (var gateway in scopeElement.Elements(Bpmn + "complexGateway"))
        {
            var id = GetId(gateway);
            var defaultFlowId = gateway.Attribute("default")?.Value;
            if (defaultFlowId is not null)
                defaultFlowIds.Add(defaultFlowId);

            var activationCondition = gateway.Attribute("activationCondition")?.Value
                ?? gateway.Element(Bpmn + "activationCondition")?.Value;

            var incomingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("targetRef")?.Value == id);
            var outgoingCount = scopeElement.Elements(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("sourceRef")?.Value == id);

            bool isFork;
            if (outgoingCount > incomingCount)
            {
                isFork = true;
            }
            else if (incomingCount > outgoingCount)
            {
                isFork = false;
            }
            else if (incomingCount <= 1)
            {
                isFork = true;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Complex gateway '{id}' has {incomingCount} incoming and {outgoingCount} outgoing flows. " +
                    "Mixed complex gateways (both join and fork) are not supported. " +
                    "Split into separate join and fork gateways.");
            }

            if (activationCondition is not null && isFork)
            {
                LogActivationConditionIgnoredOnFork(id);
                activationCondition = null;
            }

            var complexActivity = new ComplexGateway(id, IsFork: isFork, ActivationCondition: isFork ? null : activationCondition);
            activities.Add(complexActivity);
            activityMap[id] = complexActivity;
        }

        // Parse event-based gateways
        foreach (var gateway in scopeElement.Elements(Bpmn + "eventBasedGateway"))
        {
            var id = GetId(gateway);
            var activity = new EventBasedGateway(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse sub-processes (recursive). triggeredByEvent="true" produces an
        // EventSubProcess instead of a regular SubProcess.
        foreach (var subProcessEl in scopeElement.Elements(Bpmn + "subProcess"))
        {
            var id = GetId(subProcessEl);
            var childActivities = new List<Activity>();
            var childDefaultFlowIds = new HashSet<string>();
            ParseActivities(subProcessEl, childActivities, activityMap, childDefaultFlowIds, insideTransaction);

            var childSequenceFlows = new List<SequenceFlow>();
            ParseSequenceFlows(subProcessEl, childSequenceFlows, activityMap, childDefaultFlowIds);

            var triggeredByEvent = subProcessEl.Attribute("triggeredByEvent")?.Value == "true";

            Activity activity;
            if (triggeredByEvent)
            {
                // Determine isInterrupting from the event sub-process's start event.
                // Per BPMN 2.0 §10.2.4 an error start event is always interrupting,
                // regardless of any isInterrupting attribute on the XML.
                var startEventEl = subProcessEl.Element(Bpmn + "startEvent");
                var hasErrorStart = startEventEl?.Element(Bpmn + "errorEventDefinition") != null;
                var isInterruptingAttr = startEventEl?.Attribute("isInterrupting")?.Value;
                var isInterrupting = hasErrorStart || isInterruptingAttr != "false";

                if (hasErrorStart && isInterruptingAttr == "false")
                {
                    // Forced to interrupting per BPMN spec; the XML attribute is ignored.
                    // (No logger plumbed into BpmnConverter today; behaviour is correct
                    // even without the warning.)
                }

                // Multi-instance is not valid for event sub-processes per BPMN spec;
                // we therefore do not call TryWrapMultiInstance here.
                activity = new EventSubProcess(id)
                {
                    Activities = childActivities,
                    SequenceFlows = childSequenceFlows,
                    IsInterrupting = isInterrupting,
                };
            }
            else
            {
                activity = new SubProcess(id)
                {
                    Activities = childActivities,
                    SequenceFlows = childSequenceFlows
                };
                activity = TryWrapMultiInstance(subProcessEl, activity) ?? activity;
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse transaction sub-processes (recursive). A <transaction> is a distinct
        // BPMN 2.0 element — not a flag on <subProcess>. Multi-instance and nested
        // transactions are rejected at parse time.
        foreach (var transactionEl in scopeElement.Elements(Bpmn + "transaction"))
        {
            var id = GetId(transactionEl);

            if (transactionEl.Element(Bpmn + "multiInstanceLoopCharacteristics") is not null)
                throw new InvalidOperationException(
                    $"Transaction Sub-Process '{id}' does not support multi-instance loop characteristics. " +
                    "Remove the multiInstanceLoopCharacteristics element, or use a regular Sub-Process.");

            if (insideTransaction)
                throw new InvalidOperationException(
                    $"Nested Transaction Sub-Process '{id}' is not supported. " +
                    "A <transaction> cannot contain another <transaction>.");

            var childActivities = new List<Activity>();
            var childDefaultFlowIds = new HashSet<string>();
            ParseActivities(transactionEl, childActivities, activityMap, childDefaultFlowIds, insideTransaction: true);

            var childSequenceFlows = new List<SequenceFlow>();
            ParseSequenceFlows(transactionEl, childSequenceFlows, activityMap, childDefaultFlowIds);

            var activity = new Transaction(id)
            {
                Activities = childActivities,
                SequenceFlows = childSequenceFlows
            };
            // Do NOT call TryWrapMultiInstance — rejected above.
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse call activities
        foreach (var callActivityEl in scopeElement.Elements(Bpmn + "callActivity"))
        {
            var id = GetId(callActivityEl);
            var calledElement = callActivityEl.Attribute("calledElement")?.Value
                ?? throw new InvalidOperationException($"callActivity '{id}' must have a calledElement attribute");

            var propagateAllParent = ParseBoolAttribute(callActivityEl, "propagateAllParentVariables", true);
            var propagateAllChild = ParseBoolAttribute(callActivityEl, "propagateAllChildVariables", true);

            var inputMappings = new List<VariableMapping>();
            var outputMappings = new List<VariableMapping>();

            var extensionElements = callActivityEl.Element(Bpmn + "extensionElements");
            if (extensionElements != null)
            {
                foreach (var input in extensionElements.Elements().Where(e => e.Name.LocalName == "inputMapping"))
                {
                    var source = input.Attribute("source")?.Value ?? "";
                    var target = input.Attribute("target")?.Value ?? "";
                    inputMappings.Add(new VariableMapping(source, target));
                }

                foreach (var output in extensionElements.Elements().Where(e => e.Name.LocalName == "outputMapping"))
                {
                    var source = output.Attribute("source")?.Value ?? "";
                    var target = output.Attribute("target")?.Value ?? "";
                    outputMappings.Add(new VariableMapping(source, target));
                }
            }

            Activity activity = new CallActivity(id, calledElement, inputMappings, outputMappings, propagateAllParent, propagateAllChild);
            activity = TryWrapMultiInstance(callActivityEl, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse boundary events
        foreach (var boundaryEl in scopeElement.Elements(Bpmn + "boundaryEvent"))
        {
            var id = GetId(boundaryEl);
            var attachedToRef = boundaryEl.Attribute("attachedToRef")?.Value
                ?? throw new InvalidOperationException($"boundaryEvent '{id}' must have an attachedToRef attribute");

            // BPMN spec: cancelActivity defaults to true when absent
            var cancelActivityAttr = boundaryEl.Attribute("cancelActivity")?.Value;
            bool isInterrupting;
            if (cancelActivityAttr == null)
            {
                isInterrupting = true;
            }
            else if (!bool.TryParse(cancelActivityAttr, out var cancelVal))
            {
                throw new InvalidOperationException(
                    $"boundaryEvent '{id}' has invalid cancelActivity value '{cancelActivityAttr}', expected 'true' or 'false'");
            }
            else
            {
                isInterrupting = cancelVal;
            }

            var compensateDefBoundary = boundaryEl.Element(Bpmn + "compensateEventDefinition");
            var eventDefs = CollectEventDefinitions(boundaryEl, id, "boundaryEvent");

            Activity activity;
            if (eventDefs.Count > 1)
            {
                activity = new MultipleBoundaryEvent(id, attachedToRef, eventDefs, isInterrupting);
            }
            else if (compensateDefBoundary != null)
            {
                if (isInterrupting)
                    throw new InvalidOperationException(
                        $"CompensationBoundaryEvent '{id}' must not have cancelActivity=\"true\". " +
                        "Compensation boundary events are always non-interrupting per BPMN spec.");
                if (!compensationHandlerMap.TryGetValue(id, out var handlerActivityId))
                    throw new InvalidOperationException(
                        $"CompensationBoundaryEvent '{id}' has no associated handler activity. " +
                        "Add an <association> element with sourceRef='{id}' pointing to the handler.");
                activity = new CompensationBoundaryEvent(id, attachedToRef, handlerActivityId);
            }
            else
            {
                var timerDef = boundaryEl.Element(Bpmn + "timerEventDefinition");
                var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");
                var messageDef = boundaryEl.Element(Bpmn + "messageEventDefinition");
                var signalDef = boundaryEl.Element(Bpmn + "signalEventDefinition");
                var escalationDef = boundaryEl.Element(Bpmn + "escalationEventDefinition");

                if (escalationDef != null)
                {
                    // BPMN spec: escalation boundary may only be attached to SubProcess or CallActivity
                    if (activityMap.TryGetValue(attachedToRef, out var attachedActivity)
                        && attachedActivity is not SubProcess && attachedActivity is not CallActivity)
                    {
                        throw new InvalidOperationException(
                            $"boundaryEvent '{id}' escalationEventDefinition may only be attached to a SubProcess or CallActivity, not '{attachedActivity.GetType().Name}'");
                    }
                    var escalationRef = escalationDef.Attribute("escalationRef")?.Value;
                    var escalationCode = ResolveEscalationCode(scopeElement, escalationRef);
                    activity = new EscalationBoundaryEvent(id, attachedToRef, escalationCode, isInterrupting);
                }
                else if (timerDef != null)
                {
                    var timerDefinition = ParseTimerDefinition(timerDef);
                    activity = new BoundaryTimerEvent(id, attachedToRef, timerDefinition, isInterrupting);
                }
                else if (messageDef != null)
                {
                    var messageRef = messageDef.Attribute("messageRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"boundaryEvent '{id}' messageEventDefinition must have a messageRef attribute");
                    activity = new MessageBoundaryEvent(id, attachedToRef, messageRef, isInterrupting);
                }
                else if (signalDef != null)
                {
                    var signalRef = signalDef.Attribute("signalRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"boundaryEvent '{id}' signalEventDefinition must have a signalRef attribute");
                    activity = new SignalBoundaryEvent(id, attachedToRef, signalRef, isInterrupting);
                }
                else if (boundaryEl.Element(Bpmn + "cancelEventDefinition") != null)
                {
                    // Cancel boundaries are ALWAYS interrupting per BPMN spec
                    if (activityMap.TryGetValue(attachedToRef, out var attachedActivity)
                        && attachedActivity is not Transaction)
                        throw new InvalidOperationException(
                            $"CancelBoundaryEvent '{id}' may only be attached to a Transaction Sub-Process, not '{attachedActivity.GetType().Name}'");
                    activity = new CancelBoundaryEvent(id, attachedToRef, IsInterrupting: true);
                }
                else
                {
                    // Error boundaries are ALWAYS interrupting per BPMN spec
                    string? errorRef = errorDef?.Attribute("errorRef")?.Value;
                    string? errorCode = ResolveErrorCode(scopeElement, errorRef);
                    activity = new BoundaryErrorEvent(id, attachedToRef, errorCode, IsInterrupting: true);
                }
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Post-parse compensation validations
        ValidateCompensationConstraints(activities, scopeElement);
    }

    private void ValidateCompensationConstraints(List<Activity> activities, XElement scopeElement)
    {
        var compensationBoundaries = activities.OfType<CompensationBoundaryEvent>().ToList();
        if (compensationBoundaries.Count == 0) return;

        // At most one compensation boundary per activity
        var duplicates = compensationBoundaries
            .GroupBy(b => b.AttachedToActivityId)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var dup in duplicates)
            throw new InvalidOperationException(
                $"Activity '{dup.Key}' has {dup.Count()} CompensationBoundaryEvents. At most one is allowed.");

        // Collect all sequence flow targets to check handler has no incoming flow
        var sequenceFlowTargets = new HashSet<string>(
            scopeElement.Elements(Bpmn + "sequenceFlow")
                .Select(sf => sf.Attribute("targetRef")?.Value)
                .Where(v => v is not null)!);

        var handlerActivityIds = compensationBoundaries.Select(b => b.HandlerActivityId).ToHashSet();

        foreach (var boundary in compensationBoundaries)
        {
            var handlerId = boundary.HandlerActivityId;

            // Handler must not have incoming sequence flow
            if (sequenceFlowTargets.Contains(handlerId))
                throw new InvalidOperationException(
                    $"Compensation handler '{handlerId}' must not have incoming sequence flow. " +
                    "Handlers are detached — invoked only during compensation walks.");

            // Handler must not have its own CompensationBoundaryEvent (no compensation-of-compensation)
            if (compensationBoundaries.Any(b => b.AttachedToActivityId == handlerId))
                throw new InvalidOperationException(
                    $"Compensation handler '{handlerId}' must not have its own CompensationBoundaryEvent. " +
                    "Compensation of compensation is not allowed.");
        }

        // activityRef on throw/end must reference a compensable activity
        var compensableActivityIds = compensationBoundaries.Select(b => b.AttachedToActivityId).ToHashSet();

        foreach (var throwEvent in activities.OfType<CompensationIntermediateThrowEvent>())
        {
            if (throwEvent.TargetActivityRef is not null && !compensableActivityIds.Contains(throwEvent.TargetActivityRef))
                throw new InvalidOperationException(
                    $"CompensationIntermediateThrowEvent '{throwEvent.ActivityId}' targets activity " +
                    $"'{throwEvent.TargetActivityRef}' which has no CompensationBoundaryEvent attached.");
        }

        foreach (var endEvent in activities.OfType<CompensationEndEvent>())
        {
            if (endEvent is CompensationEndEvent { TargetActivityRef: not null } targeted
                && !compensableActivityIds.Contains(targeted.TargetActivityRef))
                throw new InvalidOperationException(
                    $"CompensationEndEvent '{endEvent.ActivityId}' targets activity " +
                    $"'{targeted.TargetActivityRef}' which has no CompensationBoundaryEvent attached.");
        }
    }

    // Resolves a BPMN errorRef (the id of an <error> element declared at definitions scope)
    // to the actual errorCode value. Returns null if errorRef is null/empty or cannot be found
    // (null is a valid "catch-all" value for error start/boundary events).
    private static string? ResolveErrorCode(XElement scopeElement, string? errorRef)
    {
        if (string.IsNullOrEmpty(errorRef))
        {
            return null;
        }

        var root = scopeElement.Document?.Root;
        if (root is null)
        {
            return errorRef;
        }

        var errorElement = root.Elements(Bpmn + "error")
            .FirstOrDefault(e => e.Attribute("id")?.Value == errorRef);

        return errorElement?.Attribute("errorCode")?.Value ?? errorRef;
    }

    private void ParseSequenceFlows(XElement scopeElement, List<SequenceFlow> sequenceFlows, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
    {
        foreach (var flow in scopeElement.Elements(Bpmn + "sequenceFlow"))
        {
            var flowId = GetId(flow);
            var sourceRef = flow.Attribute("sourceRef")?.Value;
            var targetRef = flow.Attribute("targetRef")?.Value;

            if (string.IsNullOrEmpty(sourceRef) || string.IsNullOrEmpty(targetRef))
                continue;

            if (!activityMap.TryGetValue(sourceRef, out var source) ||
                !activityMap.TryGetValue(targetRef, out var target))
            {
                continue; // Skip flows where source or target is not found
            }

            // Check for condition expression
            var conditionExpression = flow.Elements(Bpmn + "conditionExpression").FirstOrDefault();
            
            if (conditionExpression != null)
            {
                var condition = conditionExpression.Value.Trim();
                // Convert BPMN condition format to our format if needed
                // BPMN typically uses expressions like "${variable > value}"
                // We might need to convert to "_context.variable > value"
                condition = ConvertBpmnCondition(condition);
                sequenceFlows.Add(new ConditionalSequenceFlow(flowId, source, target, condition));
            }
            else if (defaultFlowIds.Contains(flowId))
            {
                sequenceFlows.Add(new DefaultSequenceFlow(flowId, source, target));
            }
            else
            {
                sequenceFlows.Add(new SequenceFlow(flowId, source, target));
            }
        }
    }

    private List<MessageDefinition> ParseMessages(XDocument doc)
    {
        var process = doc.Descendants(Bpmn + "process").FirstOrDefault();
        var messages = new List<MessageDefinition>();
        foreach (var msgEl in doc.Root!.Elements(Bpmn + "message"))
        {
            var id = GetId(msgEl);
            var name = msgEl.Attribute("name")?.Value
                ?? throw new InvalidOperationException($"message '{id}' must have a name attribute");

            string? correlationKey = null;

            // 1. Check <message> extension elements (Camunda 8 zeebe:subscription, fleans:subscription)
            var extensions = msgEl.Element(Bpmn + "extensionElements");
            if (extensions != null)
            {
                var zeebeSubscription = extensions.Element(Zeebe + "subscription");
                if (zeebeSubscription != null)
                {
                    correlationKey = zeebeSubscription.Attribute("correlationKey")?.Value?.TrimStart('=', ' ');
                }

                if (correlationKey == null)
                {
                    var fleansSubscription = extensions.Element(Fleans + "subscription");
                    if (fleansSubscription != null)
                    {
                        correlationKey = fleansSubscription.Attribute("correlationKey")?.Value?.TrimStart('=', ' ');
                    }
                }
            }

            // 2. Fallback: check event elements that reference this message for fleans:correlationKey attribute
            if (correlationKey == null && process != null)
            {
                correlationKey = FindCorrelationKeyOnEventElement(process, id);
            }

            messages.Add(new MessageDefinition(id, name, correlationKey));
        }
        return messages;
    }

    private static List<SignalDefinition> ParseSignals(XDocument doc)
    {
        var signals = new List<SignalDefinition>();
        foreach (var signalEl in doc.Root!.Elements(Bpmn + "signal"))
        {
            var id = signalEl.Attribute("id")?.Value
                ?? throw new InvalidOperationException("signal element must have an id attribute");
            var name = signalEl.Attribute("name")?.Value
                ?? throw new InvalidOperationException($"signal '{id}' must have a name attribute");
            signals.Add(new SignalDefinition(id, name));
        }
        return signals;
    }

    private static List<EscalationDefinition> ParseEscalations(XDocument doc)
    {
        var escalations = new List<EscalationDefinition>();
        foreach (var escEl in doc.Root!.Elements(Bpmn + "escalation"))
        {
            var id = escEl.Attribute("id")?.Value
                ?? throw new InvalidOperationException("escalation element must have an id attribute");
            var escalationCode = escEl.Attribute("escalationCode")?.Value
                ?? throw new InvalidOperationException($"escalation '{id}' must have an escalationCode attribute");
            var name = escEl.Attribute("name")?.Value;
            escalations.Add(new EscalationDefinition(id, escalationCode, name));
        }
        return escalations;
    }

    private static string? ResolveEscalationCode(XElement scopeElement, string? escalationRef)
    {
        if (string.IsNullOrEmpty(escalationRef))
            return null;

        var root = scopeElement.Document?.Root;
        if (root is null)
            return escalationRef;

        var escalationElement = root.Elements(Bpmn + "escalation")
            .FirstOrDefault(e => e.Attribute("id")?.Value == escalationRef)
            ?? throw new InvalidOperationException(
                $"Escalation definition '{escalationRef}' referenced but not found in <definitions>. "
                + "Add a <bpmn:escalation id=\"{escalationRef}\" escalationCode=\"...\"/> element.");

        return escalationElement.Attribute("escalationCode")?.Value
            ?? throw new InvalidOperationException(
                $"Escalation definition '{escalationRef}' is missing the 'escalationCode' attribute.");
    }

    private static string? FindCorrelationKeyOnEventElement(XElement process, string messageId)
    {
        // Search intermediateCatchEvent and boundaryEvent elements for fleans:correlationKey
        var eventElements = process.Descendants(Bpmn + "intermediateCatchEvent")
            .Concat(process.Descendants(Bpmn + "boundaryEvent"));

        foreach (var eventEl in eventElements)
        {
            var msgDef = eventEl.Element(Bpmn + "messageEventDefinition");
            if (msgDef?.Attribute("messageRef")?.Value == messageId)
            {
                var key = eventEl.Attribute(Fleans + "correlationKey")?.Value;
                if (key != null) return key;
            }
        }
        return null;
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex VarsGeneratedRegex();
    
    [GeneratedRegex(@"\b([a-zA-Z_][a-zA-Z0-9_]*)\b", RegexOptions.Compiled)]
    private static partial Regex BareVariableRegex();
    
    private string ConvertBpmnVariableReferences(string expression, int depth = 0)
    {
        if (depth > MaxConversionDepth)
            throw new InvalidOperationException("BPMN variable reference nesting exceeds maximum depth.");

        // Only convert ${variable} patterns to _context.variable format.
        // Does NOT convert bare variable names — scripts use _context.var explicitly
        // or rely on ${var} BPMN notation.
        return VarsGeneratedRegex().Replace(
            expression,
            match =>
            {
                var variableContent = match.Groups[1].Value.Trim();

                if (IsSimpleVariableName(variableContent))
                {
                    return $"_context.{variableContent}";
                }

                return ConvertBpmnVariableReferences(variableContent, depth + 1);
            }
        );
    }

    private string ConvertBpmnCondition(string bpmnCondition)
    {
        // BPMN conditions often use ${variable} format
        // Convert to _context.variable format for our expression evaluator
        var converted = bpmnCondition;
        
        // Step 1: Replace all ${variable} patterns with _context.variable
        // Handle both simple variables like ${amount} and complex expressions like ${amount > 100 && status == 'active'}
        converted = VarsGeneratedRegex().Replace(
            converted,
            match =>
            {
                var variableContent = match.Groups[1].Value.Trim();
                
                // If the content is a simple variable name (no operators), convert directly
                if (IsSimpleVariableName(variableContent))
                {
                    return $"_context.{variableContent}";
                }
                
                // If the content is a complex expression, recursively process it
                // This handles cases like "${amount > 100 && status == 'active'}"
                // The recursive call will process the inner expression and convert bare variables
                return ConvertBpmnCondition(variableContent);
            }
        );

        // Step 2: Convert any remaining bare variable names (not in quotes, not keywords, not already _context.*)
        converted = ConvertBareVariables(converted);

        return converted;
    }

    private static bool IsSimpleVariableName(string name)
    {
        // Matches a single identifier: letters, digits, underscores only.
        // The regex alone is sufficient — it rejects any string containing operators,
        // quotes, or other non-identifier characters.
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$");
    }

    private static string ConvertBareVariables(string expression)
    {
        // List of keywords that should not be converted
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "null", "and", "or", "not", "if", "else",
            "context", "_context", "this", "typeof", "new", "return"
        };

        return BareVariableRegex().Replace(expression, match =>
        {
            var variableName = match.Value;
            var matchIndex = match.Index;
            
            // Skip if already has _context. prefix
            if (matchIndex >= 9 && expression.Substring(matchIndex - 9, 9) == "_context.")
            {
                return variableName;
            }
            
            // Skip keywords
            if (keywords.Contains(variableName))
            {
                return variableName;
            }
            // Check if we're inside string literals by counting quotes before the match
            var singleQuotesBefore = 0;
            var doubleQuotesBefore = 0;
            for (var i = 0; i < matchIndex; i++)
            {
                if (expression[i] == '\'') singleQuotesBefore++;
                else if (expression[i] == '"') doubleQuotesBefore++;
            }

            // If we're inside quotes (odd number of quotes before means we're inside), don't convert
            if ((singleQuotesBefore % 2 == 1) || (doubleQuotesBefore % 2 == 1))
            {
                return variableName;
            }
            
            // Convert bare variable to _context.variable
            return $"_context.{variableName}";
        });
    }

    private static readonly HashSet<string> SupportedScriptFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "c#", ""
    };

    private static void ValidateScriptFormat(string activityId, string scriptFormat)
    {
        if (!SupportedScriptFormats.Contains(scriptFormat))
            throw new InvalidOperationException(
                $"ScriptTask '{activityId}' has unsupported scriptFormat '{scriptFormat}'. Supported formats: csharp, c#.");
    }

    private static bool ParseBoolAttribute(XElement element, string attributeName, bool defaultValue)
    {
        var attr = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == attributeName)?.Value;
        return attr is not null ? bool.Parse(attr) : defaultValue;
    }

    private static MultiInstanceActivity? TryWrapMultiInstance(XElement activityElement, Activity innerActivity)
    {
        var miElement = activityElement.Element(Bpmn + "multiInstanceLoopCharacteristics");
        if (miElement is null) return null;

        var isSequential = bool.TryParse(miElement.Attribute("isSequential")?.Value, out var seq) && seq;

        int? loopCardinality = null;
        var cardinalityEl = miElement.Element(Bpmn + "loopCardinality");
        if (cardinalityEl is not null && int.TryParse(cardinalityEl.Value.Trim(), out var card))
            loopCardinality = card;

        var inputCollection = miElement.Attribute(Zeebe + "collection")?.Value
            ?? miElement.Attribute("collection")?.Value;
        var inputDataItem = miElement.Attribute(Zeebe + "elementVariable")?.Value
            ?? miElement.Attribute("elementVariable")?.Value;
        var outputCollection = miElement.Attribute(Zeebe + "outputCollection")?.Value
            ?? miElement.Attribute("outputCollection")?.Value;
        var outputDataItem = miElement.Attribute(Zeebe + "outputElement")?.Value
            ?? miElement.Attribute("outputElement")?.Value;

        return new MultiInstanceActivity(
            innerActivity.ActivityId,
            innerActivity,
            isSequential,
            loopCardinality,
            inputCollection,
            inputDataItem,
            outputCollection,
            outputDataItem);
    }

    private string GetId(XElement element)
    {
        return element.Attribute("id")?.Value
            ?? throw new InvalidOperationException($"Element {element.Name} must have an id attribute");
    }

    private static List<string> ParseCommaSeparated(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).ToList();

    private static List<string>? ParseExpectedOutputs(XElement element)
    {
        var outputsElement = element.Descendants(Fleans + "expectedOutputs")
            .FirstOrDefault();
        if (outputsElement is null)
            return null;

        var outputs = outputsElement.Elements(Fleans + "output")
            .Select(e => e.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return outputs.Count > 0 ? outputs! : null;
    }

    /// <summary>
    /// Collects all event definition children from a BPMN event element.
    /// Returns a list of <see cref="EventDefinition"/> records. When the list has more
    /// than one entry the caller creates a Multiple*Event variant.
    /// </summary>
    private List<EventDefinition> CollectEventDefinitions(
        XElement eventElement, string eventId, string elementType)
    {
        var definitions = new List<EventDefinition>();

        foreach (var timerDef in eventElement.Elements(Bpmn + "timerEventDefinition"))
        {
            var timerDefinition = ParseTimerDefinition(timerDef);
            definitions.Add(new TimerEventDef(timerDefinition));
        }

        foreach (var msgDef in eventElement.Elements(Bpmn + "messageEventDefinition"))
        {
            var messageRef = msgDef.Attribute("messageRef")?.Value
                ?? throw new InvalidOperationException(
                    $"{elementType} '{eventId}' messageEventDefinition must have a messageRef attribute");
            definitions.Add(new MessageEventDef(messageRef));
        }

        foreach (var sigDef in eventElement.Elements(Bpmn + "signalEventDefinition"))
        {
            var signalRef = sigDef.Attribute("signalRef")?.Value
                ?? throw new InvalidOperationException(
                    $"{elementType} '{eventId}' signalEventDefinition must have a signalRef attribute");
            definitions.Add(new SignalEventDef(signalRef));
        }

        return definitions;
    }

    private static TimerDefinition ParseTimerDefinition(XElement timerEventDef)
    {
        var timeDuration = timerEventDef.Element(Bpmn + "timeDuration")?.Value;
        var timeDate = timerEventDef.Element(Bpmn + "timeDate")?.Value;
        var timeCycle = timerEventDef.Element(Bpmn + "timeCycle")?.Value;

        if (timeDuration != null)
            return new TimerDefinition(TimerType.Duration, timeDuration.Trim());
        if (timeDate != null)
            return new TimerDefinition(TimerType.Date, timeDate.Trim());
        if (timeCycle != null)
            return new TimerDefinition(TimerType.Cycle, timeCycle.Trim());

        throw new InvalidOperationException("timerEventDefinition must contain timeDuration, timeDate, or timeCycle");
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Warning,
        Message = "Complex gateway '{GatewayId}' has activationCondition but is detected as a fork — activationCondition is ignored on fork gateways")]
    private partial void LogActivationConditionIgnoredOnFork(string gatewayId);
}
