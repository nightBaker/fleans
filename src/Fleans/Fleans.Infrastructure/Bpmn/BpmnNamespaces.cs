using System.Xml.Linq;

namespace Fleans.Infrastructure.Bpmn;

internal static class BpmnNamespaces
{
    public static readonly XNamespace Bpmn    = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    public static readonly XNamespace Bpmndi  = "http://www.omg.org/spec/BPMN/20100524/DI";
    public static readonly XNamespace Zeebe   = "http://camunda.org/schema/zeebe/1.0";
    public static readonly XNamespace Fleans  = "http://fleans.io/schema/bpmn/fleans";
    public static readonly XNamespace Camunda = "http://camunda.org/schema/1.0/bpmn";
}
