using System.Dynamic;

namespace Fleans.Domain;

/// <summary>
/// Evaluates the <c>source</c> of a <c>fleans:input</c> / <c>fleans:output</c> mapping against a
/// variable scope: <c>=&lt;C# expression&gt;</c> (variables are top-level identifiers), or a bare
/// string literal without the leading <c>=</c>. Throws when the expression does not evaluate.
/// </summary>
public interface IMappingExpressionEvaluator
{
    object? Evaluate(string source, ExpandoObject variables);
}
