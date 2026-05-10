window.fleansModdleExtension = {
    name: "Fleans",
    prefix: "fleans",
    uri: "https://fleans.io/schema/bpmn/1.0",
    xml: {
        tagAlias: "lowerCase"
    },
    types: [
        {
            name: "CallActivityProperties",
            superClass: ["Element"],
            isAbstract: true,
            properties: [
                { name: "propagateAllParentVariables", type: "Boolean", isAttr: true, default: true },
                { name: "propagateAllChildVariables",  type: "Boolean", isAttr: true, default: true }
            ]
        },
        {
            name: "MultiInstanceProperties",
            superClass: ["Element"],
            isAbstract: true,
            properties: [
                { name: "collection",       type: "String", isAttr: true },
                { name: "elementVariable",  type: "String", isAttr: true },
                { name: "outputCollection", type: "String", isAttr: true },
                { name: "outputElement",    type: "String", isAttr: true }
            ]
        },
        {
            name: "TaskDefinition",
            superClass: ["Element"],
            properties: [
                { name: "type",    type: "String", isAttr: true },
                { name: "retries", type: "String", isAttr: true }
            ]
        },
        {
            name: "IoMapping",
            superClass: ["Element"],
            properties: [
                { name: "inputs",  type: "Input",  isMany: true },
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
        },
        // Existing flat input/output mapping shape (distinct from IoMapping above).
        // Kept for the call-activity / sub-process io path that already uses it.
        {
            name: "InputMapping",
            superClass: ["Element"],
            properties: [
                { name: "source", type: "String", isAttr: true },
                { name: "target", type: "String", isAttr: true }
            ]
        },
        {
            name: "OutputMapping",
            superClass: ["Element"],
            properties: [
                { name: "source", type: "String", isAttr: true },
                { name: "target", type: "String", isAttr: true }
            ]
        },
        {
            name: "ExpectedOutputs",
            superClass: ["Element"],
            properties: [
                { name: "outputs", type: "ExpectedOutput", isMany: true }
            ]
        },
        {
            name: "ExpectedOutput",
            superClass: ["Element"],
            properties: [
                { name: "name", type: "String", isAttr: true }
            ]
        }
    ],
    enumerations: [],
    associations: []
};
