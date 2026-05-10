// Zeebe namespace declarations for bpmn-js so the editor can structurally
// read/write <zeebe:taskDefinition>, <zeebe:ioMapping>, <zeebe:input>, <zeebe:output>,
// <zeebe:subscription>. The framework's BpmnConverter (Fleans.Infrastructure/Bpmn/BpmnConverter.cs)
// reads these elements during deploy; this extension teaches the modeler to round-trip them cleanly.
window.zeebeModdleExtension = {
    name: "Zeebe",
    prefix: "zeebe",
    uri: "http://camunda.org/schema/zeebe/1.0",
    xml: { tagAlias: "lowerCase" },
    types: [
        {
            name: "TaskDefinition",
            superClass: ["Element"],
            properties: [
                { name: "type", type: "String", isAttr: true },
                // Declared for forward-compatibility with Camunda BPMN; framework currently ignores it.
                { name: "retries", type: "String", isAttr: true }
            ]
        },
        {
            name: "IoMapping",
            superClass: ["Element"],
            properties: [
                { name: "inputs", type: "Input", isMany: true },
                { name: "outputs", type: "Output", isMany: true }
            ]
        },
        {
            name: "Input",
            superClass: ["Element"],
            properties: [
                { name: "source", type: "String", isAttr: true },
                { name: "target", type: "String", isAttr: true }
            ]
        },
        {
            name: "Output",
            superClass: ["Element"],
            properties: [
                { name: "source", type: "String", isAttr: true },
                { name: "target", type: "String", isAttr: true }
            ]
        },
        {
            name: "Subscription",
            superClass: ["Element"],
            properties: [
                { name: "correlationKey", type: "String", isAttr: true }
            ]
        }
    ]
};
