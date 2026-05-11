// Match either fleans:* or zeebe:* on a moddle element's $type for a given local name.
function _isExt(el, localName) {
    if (!el || !el.$type) return false;
    return el.$type === 'fleans:' + localName || el.$type === 'zeebe:' + localName;
}

// Read a namespaced extension attribute from a $attrs map, preferring fleans over zeebe.
function _extAttr(attrs, localName) {
    if (!attrs) return undefined;
    return attrs['fleans:' + localName] || attrs['zeebe:' + localName];
}

window.bpmnEditor = {
    _modeler: null,
    _dotNetRef: null,

    init: function (containerId, dotNetRef) {
        var container = document.getElementById(containerId);
        if (!container) return;

        if (this._modeler) {
            this._modeler.destroy();
        }

        this._dotNetRef = dotNetRef;

        this._modeler = new BpmnModeler({
            container: container,
            keyboard: { bindTo: container },
            moddleExtensions: Object.assign(
                {},
                window.fleansModdleExtension ? { fleans: window.fleansModdleExtension } : {},
                window.zeebeModdleExtension ? { zeebe: window.zeebeModdleExtension } : {}
            ),
            additionalModules: window.boundaryEventContextPadModule
                ? [window.boundaryEventContextPadModule]
                : []
        });

        if (dotNetRef) {
            var self = this;
            var eventBus = this._modeler.get('eventBus');

            eventBus.on('selection.changed', function (e) {
                var newSelection = e.newSelection;
                if (newSelection && newSelection.length === 1) {
                    var element = newSelection[0];
                    var data = self._extractElementData(element);
                    dotNetRef.invokeMethodAsync('OnElementSelected', data);
                } else {
                    dotNetRef.invokeMethodAsync('OnElementDeselected');
                }
            });
        }
    },

    _extractElementData: function (element) {
        var bo = element.businessObject;
        var data = {
            id: bo.id || '',
            type: bo.$type || '',
            name: bo.name || '',
            scriptFormat: '',
            script: '',
            conditionExpression: '',
            calledElement: '',
            propagateAllParentVariables: true,
            propagateAllChildVariables: true,
            inputMappings: [],
            outputMappings: [],
            timerType: '',
            timerExpression: '',
            hasTimerDefinition: false,
            hasMessageDefinition: false,
            messageName: '',
            correlationKey: '',
            hasSignalDefinition: false,
            signalName: '',
            hasCancelDefinition: false,
            isInterrupting: true,
            assignee: '',
            candidateGroups: [],
            candidateUsers: [],
            expectedOutputVariables: [],
            availableVariables: [],
            hasMultiInstance: false,
            isSequential: false,
            loopCardinality: '',
            inputCollection: '',
            inputDataItem: '',
            outputCollection: '',
            outputDataItem: '',
            isExecutable: false,
            documentation: '',
            isEventSubProcess: false
        };

        if (bo.$type === 'bpmn:ScriptTask') {
            data.scriptFormat = bo.scriptFormat || '';
            data.script = bo.script || '';
        }

        if (bo.$type === 'bpmn:SequenceFlow' && bo.conditionExpression) {
            data.conditionExpression = bo.conditionExpression.body || '';
        }

        if (bo.$type === 'bpmn:CallActivity') {
            data.calledElement = bo.calledElement || '';

            var attrs = bo.$attrs || {};
            data.propagateAllParentVariables = attrs['fleans:propagateAllParentVariables'] !== 'false';
            data.propagateAllChildVariables = attrs['fleans:propagateAllChildVariables'] !== 'false';

            if (bo.extensionElements && bo.extensionElements.values) {
                bo.extensionElements.values.forEach(function (ext) {
                    if (ext.$type === 'fleans:InputMapping') {
                        data.inputMappings.push({ source: ext.source || '', target: ext.target || '' });
                    } else if (ext.$type === 'fleans:OutputMapping') {
                        data.outputMappings.push({ source: ext.source || '', target: ext.target || '' });
                    }
                });
            }
        }

        if (bo.$type === 'bpmn:UserTask') {
            var attrs = bo.$attrs || {};
            data.assignee = attrs['camunda:assignee'] || '';
            var groups = attrs['camunda:candidateGroups'] || '';
            data.candidateGroups = groups ? groups.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];
            var users = attrs['camunda:candidateUsers'] || '';
            data.candidateUsers = users ? users.split(',').map(function(s) { return s.trim(); }).filter(Boolean) : [];

            if (bo.extensionElements && bo.extensionElements.values) {
                bo.extensionElements.values.forEach(function (ext) {
                    if (ext.$type === 'fleans:ExpectedOutputs' && ext.outputs) {
                        // Children are 'fleans:ExpectedOutput' (current) or 'fleans:Output' (legacy);
                        // bpmn-js exposes both via the .outputs collection regardless of inner type.
                        ext.outputs.forEach(function (output) {
                            if (output.name) data.expectedOutputVariables.push(output.name);
                        });
                    }
                });
            }
        }

        if (bo.eventDefinitions && bo.eventDefinitions.length > 0) {
            for (var i = 0; i < bo.eventDefinitions.length; i++) {
                if (bo.eventDefinitions[i].$type === 'bpmn:TimerEventDefinition') {
                    data.hasTimerDefinition = true;
                    var timerDef = bo.eventDefinitions[i];
                    if (timerDef.timeDuration) {
                        data.timerType = 'duration';
                        data.timerExpression = timerDef.timeDuration.body || '';
                    } else if (timerDef.timeDate) {
                        data.timerType = 'date';
                        data.timerExpression = timerDef.timeDate.body || '';
                    } else if (timerDef.timeCycle) {
                        data.timerType = 'cycle';
                        data.timerExpression = timerDef.timeCycle.body || '';
                    }
                    break;
                }
                if (bo.eventDefinitions[i].$type === 'bpmn:MessageEventDefinition') {
                    data.hasMessageDefinition = true;
                    var msgDef = bo.eventDefinitions[i];
                    if (msgDef.messageRef) {
                        data.messageName = msgDef.messageRef.name || '';
                    }
                    // Priority 1: fleans:Subscription or zeebe:Subscription in the message's extension elements.
                    // Strip the FEEL expression prefix ("= varName" → "varName") — Fleans normalizes
                    // to plain variable names; BpmnConverter.ParseMessages already does TrimStart('=', ' ').
                    if (msgDef.messageRef && msgDef.messageRef.extensionElements) {
                        var extVals = msgDef.messageRef.extensionElements.values || [];
                        for (var j = 0; j < extVals.length; j++) {
                            if (_isExt(extVals[j], 'Subscription')) {
                                data.correlationKey = (extVals[j].correlationKey || '').replace(/^=\s*/, '');
                                break;
                            }
                        }
                    }
                    // Priority 2: legacy fleans:correlationKey / zeebe:correlationKey attribute on the event element.
                    if (!data.correlationKey) {
                        data.correlationKey = _extAttr(bo.$attrs, 'correlationKey') || '';
                    }
                    break;
                }
                if (bo.eventDefinitions[i].$type === 'bpmn:SignalEventDefinition') {
                    data.hasSignalDefinition = true;
                    var sigDef = bo.eventDefinitions[i];
                    if (sigDef.signalRef) {
                        data.signalName = sigDef.signalRef.name || '';
                    }
                    break;
                }
                if (bo.eventDefinitions[i].$type === 'bpmn:ErrorEventDefinition') {
                    data.hasErrorDefinition = true;
                    var errDef = bo.eventDefinitions[i];
                    if (errDef.errorRef) {
                        data.errorCode = errDef.errorRef.errorCode || '';
                    }
                    break;
                }
                if (bo.eventDefinitions[i].$type === 'bpmn:CancelEventDefinition') {
                    data.hasCancelDefinition = true;
                    break;
                }
            }
        }

        if (bo.$type === 'bpmn:BoundaryEvent') {
            data.isInterrupting = bo.cancelActivity !== false;
        }

        if (bo.$type === 'bpmn:SubProcess') {
            data.isEventSubProcess = bo.triggeredByEvent === true;
        }

        var miElement = bo.loopCharacteristics;
        if (miElement && miElement.$type === 'bpmn:MultiInstanceLoopCharacteristics') {
            data.hasMultiInstance = true;
            data.isSequential = miElement.isSequential === true;

            if (miElement.loopCardinality && miElement.loopCardinality.body) {
                data.loopCardinality = miElement.loopCardinality.body;
            }

            // Namespaced attributes may land on the moddle object directly or in $attrs depending
            // on how bpmn-js stored them. Probe fleans:* first, then zeebe:*, then the unprefixed form.
            var miAttrs = miElement.$attrs || {};
            var readNs = function (localName, stdKey) {
                return miElement['fleans:' + localName]
                    || miAttrs['fleans:' + localName]
                    || miElement['zeebe:' + localName]
                    || miAttrs['zeebe:' + localName]
                    || miElement[stdKey]
                    || miAttrs[stdKey]
                    || '';
            };
            data.inputCollection  = readNs('collection',       'collection');
            data.inputDataItem    = readNs('elementVariable',  'elementVariable');
            data.outputCollection = readNs('outputCollection', 'outputCollection');
            data.outputDataItem   = readNs('outputElement',    'outputElement');
        }

        if (bo.$type === 'bpmn:Process') {
            data.isExecutable = bo.isExecutable !== false;
            data.documentation = (bo.documentation && bo.documentation[0] && bo.documentation[0].text) || '';
        }

        // Collect available variable names from ScriptTasks (only when a UserTask is selected)
        if (this._modeler && bo.$type === 'bpmn:UserTask') {
            var registry = this._modeler.get('elementRegistry');
            var vars = new Set();
            registry.forEach(function (el) {
                var elBo = el.businessObject;
                if (elBo.$type === 'bpmn:ScriptTask' && elBo.script) {
                    var matches = elBo.script.match(/variables\.(\w+)/g);
                    if (matches) {
                        matches.forEach(function (m) { vars.add(m.replace('variables.', '')); });
                    }
                }
            });
            data.availableVariables = Array.from(vars).sort();
        }

        return data;
    },

    getElementProperties: function (elementId) {
        if (!this._modeler) return null;

        var elementRegistry = this._modeler.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return null;

        return this._extractElementData(element);
    },

    updateElementId: function (oldId, newId) {
        if (!this._modeler) return null;

        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(oldId);
        if (!element) return null;

        modeling.updateProperties(element, { id: newId });

        var updated = elementRegistry.get(newId);
        if (!updated) return null;

        return this._extractElementData(updated);
    },

    updateElementProperty: function (elementId, propertyName, value) {
        if (!this._modeler) return;

        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var props = {};

        if (propertyName === 'conditionExpression') {
            if (value && value.trim() !== '') {
                props.conditionExpression = moddle.create('bpmn:FormalExpression', { body: value });
            } else {
                props.conditionExpression = undefined;
            }
        } else if (propertyName === 'propagateAllParentVariables' || propertyName === 'propagateAllChildVariables') {
            var bo = element.businessObject;
            if (!bo.$attrs) bo.$attrs = {};
            bo.$attrs['fleans:' + propertyName] = String(value);
            return;
        } else if (propertyName === 'cancelActivity') {
            props.cancelActivity = value;
        } else if (propertyName === 'camunda:assignee' || propertyName === 'camunda:candidateGroups' || propertyName === 'camunda:candidateUsers') {
            var bo = element.businessObject;
            if (!bo.$attrs) bo.$attrs = {};
            var attrUpdate = {};
            attrUpdate[propertyName] = (value && value.trim() !== '') ? value : undefined;
            modeling.updateModdleProperties(element, bo, attrUpdate);
            return;
        } else {
            props[propertyName] = value;
        }

        modeling.updateProperties(element, props);
    },

    updateExpectedOutputs: function (elementId, variableNames) {
        if (!this._modeler) return;

        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;

        // Remove existing ExpectedOutputs
        var existingExtensions = [];
        if (bo.extensionElements && bo.extensionElements.values) {
            existingExtensions = bo.extensionElements.values.filter(function (ext) {
                return ext.$type !== 'fleans:ExpectedOutputs';
            });
        }

        if (!bo.extensionElements) {
            var extElements = moddle.create('bpmn:ExtensionElements', { values: [] });
            modeling.updateProperties(element, { extensionElements: extElements });
        }

        if (variableNames && variableNames.length > 0) {
            var outputs = variableNames.map(function (name) {
                return moddle.create('fleans:ExpectedOutput', { name: name });
            });
            var expectedOutputs = moddle.create('fleans:ExpectedOutputs', { outputs: outputs });
            expectedOutputs.$parent = bo.extensionElements;

            modeling.updateModdleProperties(element, bo.extensionElements, {
                values: existingExtensions.concat([expectedOutputs])
            });
        } else {
            modeling.updateModdleProperties(element, bo.extensionElements, {
                values: existingExtensions
            });
        }
    },

    updateTimerDefinition: function (elementId, timerType, expression) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;

        var timerDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:TimerEventDefinition') {
                timerDef = bo.eventDefinitions[i];
                break;
            }
        }
        if (!timerDef) return;

        var newProps = {
            timeDuration: undefined,
            timeDate: undefined,
            timeCycle: undefined
        };

        if (expression) {
            var formalExpr = moddle.create('bpmn:FormalExpression', { body: expression });
            formalExpr.$parent = timerDef;
            if (timerType === 'duration') newProps.timeDuration = formalExpr;
            else if (timerType === 'date') newProps.timeDate = formalExpr;
            else if (timerType === 'cycle') newProps.timeCycle = formalExpr;
        }

        modeling.updateModdleProperties(element, timerDef, newProps);
    },

    updateMessageDefinition: function (elementId, messageName, correlationKey) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;

        var msgDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:MessageEventDefinition') {
                msgDef = bo.eventDefinitions[i];
                break;
            }
        }
        if (!msgDef) return;

        var canvas = this._modeler.get('canvas');
        var definitions = canvas.getRootElement().businessObject.$parent;

        if (messageName) {
            var messageEl = msgDef.messageRef;

            if (!messageEl) {
                // Create new <message> element at <definitions> level
                var msgId = 'Message_' + elementId;
                messageEl = moddle.create('bpmn:Message', { id: msgId, name: messageName });
                messageEl.$parent = definitions;

                // Use get() to access the moddle collection properly
                var rootElements = definitions.get('rootElements');
                rootElements.push(messageEl);

                // Register the ID so bpmn-js knows about it
                moddle.ids.claim(msgId, messageEl);
            } else {
                messageEl.name = messageName;
            }

            modeling.updateModdleProperties(element, msgDef, { messageRef: messageEl });
        }

        // Write correlation key as fleans:Subscription in the message element's extensionElements
        // (round-tripped structurally by bpmn-js via fleansModdleExtension). The parser also reads
        // zeebe:Subscription so legacy-prefixed siblings round-trip cleanly without our help.
        var msgEl = msgDef.messageRef;
        if (msgEl) {
            var existingExt = msgEl.extensionElements;
            // Strip BOTH prefixes when rewriting so we don't end up with two Subscription siblings.
            var otherExts = (existingExt && existingExt.values || []).filter(
                function(v) { return !_isExt(v, 'Subscription'); }
            );
            if (correlationKey) {
                var subscription = moddle.create('fleans:Subscription', { correlationKey: correlationKey });
                otherExts.push(subscription);
            }
            var newExt = moddle.create('bpmn:ExtensionElements', { values: otherExts });
            modeling.updateModdleProperties(element, msgEl, { extensionElements: newExt });
        }

        // Remove legacy *:correlationKey attribute from the event element so it cannot shadow
        // the Subscription value on the next read cycle (migration path for old BPMNs).
        if (!bo.$attrs) bo.$attrs = {};
        delete bo.$attrs['fleans:correlationKey'];
        delete bo.$attrs['zeebe:correlationKey'];
    },

    updateSignalDefinition: function (elementId, signalName) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;

        var sigDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:SignalEventDefinition') {
                sigDef = bo.eventDefinitions[i];
                break;
            }
        }
        if (!sigDef) return;

        var canvas = this._modeler.get('canvas');
        var definitions = canvas.getRootElement().businessObject.$parent;

        if (signalName) {
            var signalEl = sigDef.signalRef;

            if (!signalEl) {
                var sigId = 'Signal_' + elementId;
                signalEl = moddle.create('bpmn:Signal', { id: sigId, name: signalName });
                signalEl.$parent = definitions;

                var rootElements = definitions.get('rootElements');
                rootElements.push(signalEl);

                moddle.ids.claim(sigId, signalEl);
            } else {
                signalEl.name = signalName;
            }

            modeling.updateModdleProperties(element, sigDef, { signalRef: signalEl });
        } else {
            modeling.updateModdleProperties(element, sigDef, { signalRef: undefined });
        }
    },

    updateErrorDefinition: function (elementId, errorCode) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;

        var errDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:ErrorEventDefinition') {
                errDef = bo.eventDefinitions[i];
                break;
            }
        }
        if (!errDef) return;

        var canvas = this._modeler.get('canvas');
        var definitions = canvas.getRootElement().businessObject.$parent;

        if (errorCode) {
            var errorEl = errDef.errorRef;

            if (!errorEl) {
                var errId = 'Error_' + elementId;
                errorEl = moddle.create('bpmn:Error', { id: errId, errorCode: errorCode });
                errorEl.$parent = definitions;

                var rootElements = definitions.get('rootElements');
                rootElements.push(errorEl);

                moddle.ids.claim(errId, errorEl);
            } else {
                errorEl.errorCode = errorCode;
            }

            modeling.updateModdleProperties(element, errDef, { errorRef: errorEl });
        } else {
            modeling.updateModdleProperties(element, errDef, { errorRef: undefined });
        }
    },

    enableMultiInstance: function (elementId) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (bo.loopCharacteristics) return;

        var mi = moddle.create('bpmn:MultiInstanceLoopCharacteristics', {
            isSequential: false
        });
        mi.$parent = bo;
        modeling.updateProperties(element, { loopCharacteristics: mi });
    },

    removeMultiInstance: function (elementId) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        modeling.updateProperties(element, { loopCharacteristics: undefined });
    },

    updateMultiInstanceProperty: function (elementId, property, value) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        var mi = bo.loopCharacteristics;
        if (!mi) return;

        switch (property) {
            case 'isSequential':
                modeling.updateModdleProperties(element, mi, { isSequential: !!value });
                break;

            case 'loopCardinality':
                if (value && value.trim() !== '') {
                    var expr = moddle.create('bpmn:FormalExpression', { body: value });
                    expr.$parent = mi;
                    modeling.updateModdleProperties(element, mi, { loopCardinality: expr });
                } else {
                    modeling.updateModdleProperties(element, mi, { loopCardinality: undefined });
                }
                break;

            case 'inputCollection':
            case 'inputDataItem':
            case 'outputCollection':
            case 'outputDataItem': {
                var keyMap = {
                    inputCollection:  'collection',
                    inputDataItem:    'elementVariable',
                    outputCollection: 'outputCollection',
                    outputDataItem:   'outputElement'
                };
                var localName = keyMap[property];
                var normalized = (value && value.trim() !== '') ? value : undefined;
                var attrUpdate = {};
                attrUpdate['fleans:' + localName] = normalized;
                // Clear any legacy zeebe-prefixed or unprefixed copies so reads stay deterministic.
                attrUpdate['zeebe:' + localName] = undefined;
                attrUpdate[localName] = undefined;
                modeling.updateModdleProperties(element, mi, attrUpdate);
                break;
            }
        }
    },

    setIsExecutable: function (elementId, value) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        modeling.updateProperties(element, { isExecutable: !!value });
    },

    setDocumentation: function (elementId, text) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;
        var moddle = this._modeler.get('moddle');
        if (text) {
            var docEl = moddle.create('bpmn:Documentation', { text: text });
            modeling.updateModdleProperties(element, bo, { documentation: [docEl] });
        } else {
            modeling.updateModdleProperties(element, bo, { documentation: [] });
        }
    },

    getProcessId: function () {
        if (!this._modeler) return null;
        var canvas = this._modeler.get('canvas');
        var rootElement = canvas.getRootElement();
        return rootElement.businessObject.id;
    },

    updateProcessId: function (newId) {
        if (!this._modeler) return false;
        var canvas = this._modeler.get('canvas');
        var rootElement = canvas.getRootElement();
        var modeling = this._modeler.get('modeling');
        modeling.updateProperties(rootElement, { id: newId });
        return true;
    },

    replaceElement: function (elementId, newType) {
        if (!this._modeler) return null;

        var elementRegistry = this._modeler.get('elementRegistry');
        var bpmnReplace = this._modeler.get('bpmnReplace');
        var selection = this._modeler.get('selection');
        var element = elementRegistry.get(elementId);
        if (!element) return null;

        var newElement = bpmnReplace.replaceElement(element, { type: newType });

        selection.select(newElement);

        return this._extractElementData(newElement);
    },

    addMapping: function (elementId, type) {
        if (!this._modeler) return;

        var elementRegistry = this._modeler.get('elementRegistry');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.extensionElements) {
            bo.extensionElements = moddle.create('bpmn:ExtensionElements', { values: [] });
            bo.extensionElements.$parent = bo;
        }

        var mappingType = type === 'input' ? 'fleans:InputMapping' : 'fleans:OutputMapping';
        var mapping = moddle.create(mappingType, { source: '', target: '' });
        mapping.$parent = bo.extensionElements;
        bo.extensionElements.values.push(mapping);
    },

    removeMapping: function (elementId, type, index) {
        if (!this._modeler) return;

        var elementRegistry = this._modeler.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.extensionElements || !bo.extensionElements.values) return;

        var mappingType = type === 'input' ? 'fleans:InputMapping' : 'fleans:OutputMapping';
        var mappings = bo.extensionElements.values.filter(function (v) { return v.$type === mappingType; });
        if (index >= 0 && index < mappings.length) {
            var idx = bo.extensionElements.values.indexOf(mappings[index]);
            if (idx >= 0) {
                bo.extensionElements.values.splice(idx, 1);
            }
        }
    },

    updateMapping: function (elementId, type, index, source, target) {
        if (!this._modeler) return;

        var elementRegistry = this._modeler.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.extensionElements || !bo.extensionElements.values) return;

        var mappingType = type === 'input' ? 'fleans:InputMapping' : 'fleans:OutputMapping';
        var mappings = bo.extensionElements.values.filter(function (v) { return v.$type === mappingType; });
        if (index >= 0 && index < mappings.length) {
            mappings[index].source = source;
            mappings[index].target = target;
        }
    },

    // --- Custom-task plugin support (sub-issue C of #357) ---
    //
    // The framework parses <fleans:taskDefinition type="…"> (preferred) or <zeebe:taskDefinition>
    // (legacy/imported) as the plugin discriminator, and <fleans:ioMapping>/<zeebe:ioMapping>
    // children as per-parameter inputs/outputs. These helpers create new elements with the
    // fleans: prefix and read either prefix so existing BPMN round-trips cleanly.

    getServiceTaskType: function (elementId) {
        if (!this._modeler) return null;
        var elementRegistry = this._modeler.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return null;
        var bo = element.businessObject;

        var ext = bo.extensionElements && bo.extensionElements.values;
        if (ext && ext.length) {
            for (var i = 0; i < ext.length; i++) {
                if (_isExt(ext[i], 'TaskDefinition')) return ext[i].type || null;
            }
        }
        // Fallback: bare `type=` attribute on <serviceTask> from hand-authored BPMN.
        if (bo.$attrs && typeof bo.$attrs.type === 'string') return bo.$attrs.type;
        if (typeof bo.type === 'string' && bo.type !== bo.$type) return bo.type;
        return null;
    },

    setServiceTaskType: function (elementId, taskType) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;

        var ext = bo.extensionElements;
        if (!ext) {
            ext = moddle.create('bpmn:ExtensionElements', { values: [] });
            ext.$parent = bo;
        }
        var values = ext.values || [];

        // Upsert the TaskDefinition entry. Edits in place if a (zeebe-)prefixed entry exists,
        // otherwise creates a new fleans:TaskDefinition.
        var taskDef = null;
        for (var i = 0; i < values.length; i++) {
            if (_isExt(values[i], 'TaskDefinition')) { taskDef = values[i]; break; }
        }
        if (!taskType) {
            // Clear case — remove any TaskDefinition entry regardless of prefix.
            ext.values = values.filter(function (v) { return !_isExt(v, 'TaskDefinition'); });
        } else if (!taskDef) {
            taskDef = moddle.create('fleans:TaskDefinition', { type: taskType });
            taskDef.$parent = ext;
            ext.values = values.concat([taskDef]);
        } else {
            taskDef.type = taskType;
        }

        modeling.updateProperties(element, { extensionElements: ext });
    },

    getIoMappings: function (elementId) {
        if (!this._modeler) return [];
        var elementRegistry = this._modeler.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return [];
        var bo = element.businessObject;
        var ext = bo.extensionElements && bo.extensionElements.values;
        if (!ext || !ext.length) return [];

        var ioMapping = null;
        for (var i = 0; i < ext.length; i++) {
            if (_isExt(ext[i], 'IoMapping')) { ioMapping = ext[i]; break; }
        }
        if (!ioMapping) return [];

        var inputs = (ioMapping.inputs || []).map(function (m) {
            return { kind: 'input', source: m.source || '', target: m.target || '' };
        });
        var outputs = (ioMapping.outputs || []).map(function (m) {
            return { kind: 'output', source: m.source || '', target: m.target || '' };
        });
        return inputs.concat(outputs);
    },

    setIoMappingInput: function (elementId, target, source) {
        if (!this._modeler) return;
        if (!target) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;

        var ext = bo.extensionElements;
        if (!ext) {
            ext = moddle.create('bpmn:ExtensionElements', { values: [] });
            ext.$parent = bo;
        }
        var values = ext.values || [];

        var ioMapping = null;
        for (var i = 0; i < values.length; i++) {
            if (_isExt(values[i], 'IoMapping')) { ioMapping = values[i]; break; }
        }
        if (!ioMapping) {
            ioMapping = moddle.create('fleans:IoMapping', { inputs: [], outputs: [] });
            ioMapping.$parent = ext;
            ext.values = values.concat([ioMapping]);
        }

        // Match incoming children's prefix to the IoMapping container's prefix so the saved XML stays
        // namespace-consistent (avoids mixed-prefix children inside a single ioMapping).
        var prefix = ioMapping.$type.indexOf('fleans:') === 0 ? 'fleans' : 'zeebe';

        var inputs = ioMapping.inputs || [];
        var existing = null;
        for (var j = 0; j < inputs.length; j++) {
            if (inputs[j].target === target) { existing = inputs[j]; break; }
        }
        if (existing) {
            existing.source = source || '';
        } else {
            var input = moddle.create(prefix + ':Input', { source: source || '', target: target });
            input.$parent = ioMapping;
            ioMapping.inputs = inputs.concat([input]);
        }

        modeling.updateProperties(element, { extensionElements: ext });
    },

    removeIoMappingInput: function (elementId, target) {
        if (!this._modeler) return;
        if (!target) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;

        var ext = bo.extensionElements && bo.extensionElements.values;
        if (!ext || !ext.length) return;

        var ioMapping = null;
        for (var i = 0; i < ext.length; i++) {
            if (_isExt(ext[i], 'IoMapping')) { ioMapping = ext[i]; break; }
        }
        if (!ioMapping || !ioMapping.inputs) return;

        ioMapping.inputs = ioMapping.inputs.filter(function (m) { return m.target !== target; });

        modeling.updateProperties(element, { extensionElements: bo.extensionElements });
    },

    clearAllIoMappings: function (elementId) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;

        var ext = bo.extensionElements && bo.extensionElements.values;
        if (!ext || !ext.length) return;

        var newValues = ext.filter(function (v) { return !_isExt(v, 'IoMapping'); });
        bo.extensionElements.values = newValues;
        modeling.updateProperties(element, { extensionElements: bo.extensionElements });
    },

    loadXml: async function (bpmnXml) {
        if (!this._modeler) return;

        // bpmn-js fires commandStack.changed from CommandStack.clear() inside
        // importXML. Suppress dirty notifications for the duration of the load
        // so tab-switching or restoring doesn't flag the incoming tab dirty.
        this._suppressDirty = true;
        try {
            await this._modeler.importXML(bpmnXml);
            var canvas = this._modeler.get('canvas');
            canvas.zoom('fit-viewport');
        } catch (err) {
            console.error('Failed to load BPMN XML', err);
            throw err;
        } finally {
            this._suppressDirty = false;
        }
    },

    newDiagram: async function () {
        var xml = '<?xml version="1.0" encoding="UTF-8"?>' +
            '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" ' +
            'xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI" ' +
            'xmlns:dc="http://www.omg.org/spec/DD/20100524/DC" ' +
            'xmlns:zeebe="http://camunda.org/schema/zeebe/1.0" ' +
            'id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">' +
            '<bpmn:process id="Process_1" isExecutable="true">' +
            '<bpmn:startEvent id="StartEvent_1" />' +
            '</bpmn:process>' +
            '<bpmndi:BPMNDiagram id="BPMNDiagram_1">' +
            '<bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="Process_1">' +
            '<bpmndi:BPMNShape id="_BPMNShape_StartEvent_1" bpmnElement="StartEvent_1">' +
            '<dc:Bounds x="179" y="159" width="36" height="36" />' +
            '</bpmndi:BPMNShape>' +
            '</bpmndi:BPMNPlane>' +
            '</bpmndi:BPMNDiagram>' +
            '</bpmn:definitions>';

        await this.loadXml(xml);
    },

    getXml: async function () {
        if (!this._modeler) return null;

        try {
            var result = await this._modeler.saveXML({ format: true });
            return result.xml;
        } catch (err) {
            console.error('Failed to export BPMN XML', err);
            throw err;
        }
    },

    downloadXml: function (base64, filename) {
        var a = document.createElement('a');
        a.href = 'data:application/xml;base64,' + base64;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    },

    destroy: function () {
        if (this._modeler) {
            this._modeler.destroy();
            this._modeler = null;
        }
        this._dotNetRef = null;
        this._dirtyListenerAttached = false;
        this._suppressDirty = false;
    },

    registerDirtyCallback: function (dotNetRef) {
        if (!this._modeler || !dotNetRef) return;
        if (this._dirtyListenerAttached) return;
        var eventBus = this._modeler.get('eventBus');
        var self = this;
        eventBus.on('commandStack.changed', function () {
            if (self._suppressDirty) return;
            dotNetRef.invokeMethodAsync('OnModelerDirty');
        });
        this._dirtyListenerAttached = true;
    },

    readStorage: function (key) {
        try {
            return window.localStorage.getItem(key);
        } catch (err) {
            return null;
        }
    },

    writeStorage: function (key, value) {
        try {
            window.localStorage.setItem(key, value);
            return true;
        } catch (err) {
            return false;
        }
    },

    registerBeforeUnloadWarning: function (dotNetRef) {
        if (this._beforeUnloadAttached) return;
        this._beforeUnloadAttached = true;
        var self = this;
        window.addEventListener('beforeunload', function (event) {
            if (self._hasDirtyTabs) {
                event.preventDefault();
                event.returnValue = '';
                return '';
            }
        });
    },

    setDirtyTabsFlag: function (hasDirty) {
        this._hasDirtyTabs = !!hasDirty;
    }
};
