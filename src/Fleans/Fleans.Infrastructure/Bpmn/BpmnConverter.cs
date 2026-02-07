using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Infrastructure.Bpmn;

public partial class BpmnConverter : IBpmnConverter
{
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string BpmndiNamespace = "http://www.omg.org/spec/BPMN/20100524/DI";
    private static readonly XNamespace Bpmn = BpmnNamespace;
    private static readonly XNamespace Bpmndi = BpmndiNamespace;

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

        // Parse activities
        ParseActivities(process, activities, activityMap);

        // Parse sequence flows
        ParseSequenceFlows(process, sequenceFlows, activityMap);

        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = sequenceFlows
        };

        return workflow;
    }

    private void ParseActivities(XElement process, List<Activity> activities, Dictionary<string, Activity> activityMap)
    {
        // Parse start events
        foreach (var startEvent in process.Descendants(Bpmn + "startEvent"))
        {
            var id = GetId(startEvent);
            var activity = new StartEvent(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse end events
        foreach (var endEvent in process.Descendants(Bpmn + "endEvent"))
        {
            var id = GetId(endEvent);
            var activity = new EndEvent(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse tasks
        foreach (var task in process.Descendants(Bpmn + "task"))
        {
            var id = GetId(task);
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse user tasks
        foreach (var userTask in process.Descendants(Bpmn + "userTask"))
        {
            var id = GetId(userTask);
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse service tasks
        foreach (var serviceTask in process.Descendants(Bpmn + "serviceTask"))
        {
            var id = GetId(serviceTask);
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse script tasks
        foreach (var scriptTask in process.Descendants(Bpmn + "scriptTask"))
        {
            var id = GetId(scriptTask);
            var scriptFormat = scriptTask.Attribute("scriptFormat")?.Value ?? "csharp";
            var scriptElement = scriptTask.Element(Bpmn + "script");
            var script = scriptElement?.Value.Trim() ?? "";
            script = ConvertBpmnVariableReferences(script);
            var activity = new ScriptTask(id, script, scriptFormat);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse exclusive gateways
        foreach (var gateway in process.Descendants(Bpmn + "exclusiveGateway"))
        {
            var id = GetId(gateway);
            var activity = new ExclusiveGateway(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }

        // Parse parallel gateways (fork)
        foreach (var gateway in process.Descendants(Bpmn + "parallelGateway"))
        {
            var id = GetId(gateway);
            // Determine if it's a fork or join by checking incoming/outgoing flows
            var incomingCount = process.Descendants(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("targetRef")?.Value == id);
            var outgoingCount = process.Descendants(Bpmn + "sequenceFlow")
                .Count(sf => sf.Attribute("sourceRef")?.Value == id);
            
            //TODO [nitpick] The determination of whether a parallel gateway is a fork or join uses a simple count comparison,
            //but this logic may not handle edge cases correctly (e.g., when counts are equal).
            //Consider documenting the expected behavior when incoming and outgoing counts are equal,
            //or adding explicit handling for this case to make the logic clearer and more maintainable.
            var isFork = outgoingCount > incomingCount;
            var activity = new ParallelGateway(id, isFork);
            activities.Add(activity);
            activityMap[id] = activity;
        }
    }

    private void ParseSequenceFlows(XElement process, List<SequenceFlow> sequenceFlows, Dictionary<string, Activity> activityMap)
    {
        foreach (var flow in process.Descendants(Bpmn + "sequenceFlow"))
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
            var conditionExpression = flow.Descendants(Bpmn + "conditionExpression").FirstOrDefault();
            
            if (conditionExpression != null)
            {
                var condition = conditionExpression.Value.Trim();
                // Convert BPMN condition format to our format if needed
                // BPMN typically uses expressions like "${variable > value}"
                // We might need to convert to "_context.variable > value"
                condition = ConvertBpmnCondition(condition);
                sequenceFlows.Add(new ConditionalSequenceFlow(flowId, source, target, condition));
            }
            else
            {
                sequenceFlows.Add(new SequenceFlow(flowId, source, target));
            }
        }
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex VarsGeneratedRegex();
    
    [GeneratedRegex(@"\b([a-zA-Z_][a-zA-Z0-9_]*)\b", RegexOptions.Compiled)]
    private static partial Regex BareVariableRegex();
    
    private string ConvertBpmnVariableReferences(string expression)
    {
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

                return ConvertBpmnVariableReferences(variableContent);
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
        // Check if it's a simple variable name (no operators, no complex expressions)
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$") &&
               !name.Contains(">") && !name.Contains("<") && !name.Contains("=") &&
               !name.Contains("&&") && !name.Contains("||") && !name.Contains("!") &&
               !name.Contains("+") && !name.Contains("-") && !name.Contains("*") &&
               !name.Contains("/") && !name.Contains("(") && !name.Contains(")") &&
               !name.Contains("'") && !name.Contains("\"");
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
            //TODO optimize: Check if the variable is a number Creating substrings for quote counting is inefficient.
            //Instead of creating two substring allocations and then counting characters, iterate through the expression once up to matchIndex to count quotes.
            //This reduces memory allocations and improves performance for condition conversion.
            
            // Check if we're inside string literals
            var beforeMatch = expression.Substring(0, matchIndex);
            var afterMatch = expression.Substring(matchIndex + match.Length);
            
            // Count quotes before and after
            var singleQuotesBefore = beforeMatch.Count(c => c == '\'');
            var singleQuotesAfter = afterMatch.Count(c => c == '\'');
            var doubleQuotesBefore = beforeMatch.Count(c => c == '"');
            var doubleQuotesAfter = afterMatch.Count(c => c == '"');
            
            // If we're inside quotes (odd number of quotes before means we're inside), don't convert
            if ((singleQuotesBefore % 2 == 1) || (doubleQuotesBefore % 2 == 1))
            {
                return variableName;
            }
            
            // Convert bare variable to _context.variable
            return $"_context.{variableName}";
        });
    }

    private string GetId(XElement element)
    {
        return element.Attribute("id")?.Value
            ?? throw new InvalidOperationException($"Element {element.Name} must have an id attribute");
    }
}