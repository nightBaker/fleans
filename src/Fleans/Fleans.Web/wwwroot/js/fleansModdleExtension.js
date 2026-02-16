window.fleansModdleExtension = {
    name: "Fleans",
    prefix: "fleans",
    uri: "http://fleans.io/schema/bpmn/fleans",
    xml: {
        tagAlias: "lowerCase"
    },
    types: [
        {
            name: "CallActivityProperties",
            superClass: ["Element"],
            isAbstract: true,
            properties: [
                {
                    name: "propagateAllParentVariables",
                    type: "Boolean",
                    isAttr: true,
                    default: true
                },
                {
                    name: "propagateAllChildVariables",
                    type: "Boolean",
                    isAttr: true,
                    default: true
                }
            ]
        },
        {
            name: "InputMapping",
            superClass: ["Element"],
            properties: [
                {
                    name: "source",
                    type: "String",
                    isAttr: true
                },
                {
                    name: "target",
                    type: "String",
                    isAttr: true
                }
            ]
        },
        {
            name: "OutputMapping",
            superClass: ["Element"],
            properties: [
                {
                    name: "source",
                    type: "String",
                    isAttr: true
                },
                {
                    name: "target",
                    type: "String",
                    isAttr: true
                }
            ]
        }
    ],
    enumerations: [],
    associations: []
};
