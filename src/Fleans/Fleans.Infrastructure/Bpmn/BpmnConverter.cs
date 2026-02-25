using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Infrastructure.Bpmn;

public partial class BpmnConverter : IBpmnConverter
{
    private const int MaxConversionDepth = 10;
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string BpmndiNamespace = "http://www.omg.org/spec/BPMN/20100524/DI";
    private const string ZeebeNamespace = "http://camunda.org/schema/zeebe/1.0";
    private const string FleansNamespace = "http://fleans.io/schema/bpmn/fleans";
    private static readonly XNamespace Bpmn = BpmnNamespace;
    private static readonly XNamespace Bpmndi = BpmndiNamespace;
    private static readonly XNamespace Zeebe = ZeebeNamespace;
    private static readonly XNamespace Fleans = FleansNamespace;

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
            Signals = signals
        };

        return workflow;
    }

    private void ParseActivities(XElement scopeElement, List<Activity> activities, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
    {
        // Parse start events (with optional timer definition)
        foreach (var startEvent in scopeElement.Elements(Bpmn + "startEvent"))
        {
            var id = GetId(startEvent);
            var timerDef = startEvent.Element(Bpmn + "timerEventDefinition");

            Activity activity;
            if (timerDef != null)
            {
                var timerDefinition = ParseTimerDefinition(timerDef);
                activity = new TimerStartEvent(id, timerDefinition);
            }
            else
            {
                activity = new StartEvent(id);
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse intermediate catch events (timer, message)
        foreach (var catchEvent in scopeElement.Elements(Bpmn + "intermediateCatchEvent"))
        {
            var id = GetId(catchEvent);
            var timerDef = catchEvent.Element(Bpmn + "timerEventDefinition");
            var messageDef = catchEvent.Element(Bpmn + "messageEventDefinition");

            if (timerDef != null)
            {
                var timerDefinition = ParseTimerDefinition(timerDef);
                var activity = new TimerIntermediateCatchEvent(id, timerDefinition);
                activities.Add(activity);
                activityMap[id] = activity;
            }
            else if (messageDef != null)
            {
                var messageRef = messageDef.Attribute("messageRef")?.Value
                    ?? throw new InvalidOperationException(
                        $"IntermediateCatchEvent '{id}' messageEventDefinition must have a messageRef attribute");
                var activity = new MessageIntermediateCatchEvent(id, messageRef);
                activities.Add(activity);
                activityMap[id] = activity;
            }
            else
            {
                var signalDef = catchEvent.Element(Bpmn + "signalEventDefinition");
                if (signalDef != null)
                {
                    var signalRef = signalDef.Attribute("signalRef")?.Value
                        ?? throw new InvalidOperationException(
                            $"IntermediateCatchEvent '{id}' signalEventDefinition must have a signalRef attribute");
                    var activity = new SignalIntermediateCatchEvent(id, signalRef);
                    activities.Add(activity);
                    activityMap[id] = activity;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"IntermediateCatchEvent '{id}' has an unsupported event definition.");
                }
            }
        }

        // Parse intermediate throw events (signal)
        foreach (var throwEvent in scopeElement.Elements(Bpmn + "intermediateThrowEvent"))
        {
            var id = GetId(throwEvent);
            var signalDef = throwEvent.Element(Bpmn + "signalEventDefinition");
            if (signalDef != null)
            {
                var signalRef = signalDef.Attribute("signalRef")?.Value
                    ?? throw new InvalidOperationException(
                        $"IntermediateThrowEvent '{id}' signalEventDefinition must have a signalRef attribute");
                var activity = new SignalIntermediateThrowEvent(id, signalRef);
                activities.Add(activity);
                activityMap[id] = activity;
            }
            else
            {
                throw new InvalidOperationException(
                    $"IntermediateThrowEvent '{id}' has an unsupported event definition.");
            }
        }

        // Parse end events
        foreach (var endEvent in scopeElement.Elements(Bpmn + "endEvent"))
        {
            var id = GetId(endEvent);
            var activity = new EndEvent(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse tasks
        foreach (var task in scopeElement.Elements(Bpmn + "task"))
        {
            var id = GetId(task);
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse user tasks
        foreach (var userTask in scopeElement.Elements(Bpmn + "userTask"))
        {
            var id = GetId(userTask);
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse service tasks
        foreach (var serviceTask in scopeElement.Elements(Bpmn + "serviceTask"))
        {
            var id = GetId(serviceTask);
            var activity = new TaskActivity(id);
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
            var activity = new ScriptTask(id, script, scriptFormat);
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

        // Parse event-based gateways
        foreach (var gateway in scopeElement.Elements(Bpmn + "eventBasedGateway"))
        {
            var id = GetId(gateway);
            var activity = new EventBasedGateway(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse sub-processes (recursive)
        foreach (var subProcessEl in scopeElement.Elements(Bpmn + "subProcess"))
        {
            var id = GetId(subProcessEl);
            var childActivities = new List<Activity>();
            var childDefaultFlowIds = new HashSet<string>();
            ParseActivities(subProcessEl, childActivities, activityMap, childDefaultFlowIds);

            var childSequenceFlows = new List<SequenceFlow>();
            ParseSequenceFlows(subProcessEl, childSequenceFlows, activityMap, childDefaultFlowIds);

            var activity = new SubProcess(id)
            {
                Activities = childActivities,
                SequenceFlows = childSequenceFlows
            };
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

            var activity = new CallActivity(id, calledElement, inputMappings, outputMappings, propagateAllParent, propagateAllChild);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse boundary events
        foreach (var boundaryEl in scopeElement.Elements(Bpmn + "boundaryEvent"))
        {
            var id = GetId(boundaryEl);
            var attachedToRef = boundaryEl.Attribute("attachedToRef")?.Value
                ?? throw new InvalidOperationException($"boundaryEvent '{id}' must have an attachedToRef attribute");

            var timerDef = boundaryEl.Element(Bpmn + "timerEventDefinition");
            var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");
            var messageDef = boundaryEl.Element(Bpmn + "messageEventDefinition");
            var signalDef = boundaryEl.Element(Bpmn + "signalEventDefinition");

            Activity activity;
            if (timerDef != null)
            {
                var timerDefinition = ParseTimerDefinition(timerDef);
                activity = new BoundaryTimerEvent(id, attachedToRef, timerDefinition);
            }
            else if (messageDef != null)
            {
                var messageRef = messageDef.Attribute("messageRef")?.Value
                    ?? throw new InvalidOperationException(
                        $"boundaryEvent '{id}' messageEventDefinition must have a messageRef attribute");
                activity = new MessageBoundaryEvent(id, attachedToRef, messageRef);
            }
            else if (signalDef != null)
            {
                var signalRef = signalDef.Attribute("signalRef")?.Value
                    ?? throw new InvalidOperationException(
                        $"boundaryEvent '{id}' signalEventDefinition must have a signalRef attribute");
                activity = new SignalBoundaryEvent(id, attachedToRef, signalRef);
            }
            else
            {
                string? errorCode = errorDef?.Attribute("errorRef")?.Value;
                activity = new BoundaryErrorEvent(id, attachedToRef, errorCode);
            }

            activities.Add(activity);
            activityMap[id] = activity;
        }
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

    private string GetId(XElement element)
    {
        return element.Attribute("id")?.Value
            ?? throw new InvalidOperationException($"Element {element.Name} must have an id attribute");
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
}