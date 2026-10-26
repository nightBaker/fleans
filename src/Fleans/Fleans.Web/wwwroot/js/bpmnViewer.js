window.bpmnViewer = {
    _viewer: null,
    _dotNetRef: null,
    _clickHandlerRegistered: false,

    _activityTypes: [
        'bpmn:Task', 'bpmn:UserTask', 'bpmn:ServiceTask', 'bpmn:ScriptTask',
        'bpmn:SendTask', 'bpmn:ReceiveTask', 'bpmn:ManualTask', 'bpmn:BusinessRuleTask',
        'bpmn:StartEvent', 'bpmn:EndEvent', 'bpmn:IntermediateCatchEvent', 'bpmn:IntermediateThrowEvent',
        'bpmn:BoundaryEvent', 'bpmn:ExclusiveGateway', 'bpmn:ParallelGateway',
        'bpmn:InclusiveGateway', 'bpmn:EventBasedGateway', 'bpmn:SubProcess',
        'bpmn:CallActivity', 'bpmn:SequenceFlow'
    ],

    init: async function (containerId, bpmnXml) {
        const container = document.getElementById(containerId);
        if (!container) return;

        if (this._viewer) {
            this._viewer.destroy();
        }

        this._clickHandlerRegistered = false;
        // Register the same moddle extensions as the editor so that bpmn-js
        // parses <fleans:taskDefinition>, <fleans:ioMapping>, <zeebe:*>, etc.
        // as typed moddle elements instead of opaque $children blobs. Without
        // this, the property-extraction helpers (which match on PascalCase
        // $type and read typed `.inputs`/`.outputs`/`.type` properties) come
        // back empty even though the XML has the data.
        this._viewer = window.fleansBpmnTheme.create(BpmnViewer, {
            container: container,
            moddleExtensions: Object.assign(
                {},
                window.fleansModdleExtension ? { fleans: window.fleansModdleExtension } : {},
                window.zeebeModdleExtension ? { zeebe: window.zeebeModdleExtension } : {}
            )
        });

        try {
            await window.fleansAutoLayout.importXml(this._viewer, bpmnXml);
            const canvas = this._viewer.get('canvas');
            canvas.zoom('fit-viewport');
        } catch (err) {
            console.error('Failed to render BPMN diagram', err);
        }
    },

    registerClickHandler: function (dotNetRef) {
        this._dotNetRef = dotNetRef;
        if (!this._viewer || this._clickHandlerRegistered) return;
        this._clickHandlerRegistered = true;

        var self = this;
        var eventBus = this._viewer.get('eventBus');
        eventBus.on('element.click', function (e) {
            if (self._activityTypes.indexOf(e.element.type) === -1) {
                self.clearSelection();
                return;
            }
            self.selectElement(e.element.id);
            if (self._dotNetRef) {
                self._dotNetRef.invokeMethodAsync('OnBpmnElementClicked', e.element.id);
            }
        });
    },

    highlight: function (activeIds, errorIds, lastErrorId, cancelledIds, completedIds) {
        if (!this._viewer) return;

        const canvas = this._viewer.get('canvas');
        const elementRegistry = this._viewer.get('elementRegistry');
        const overlays = this._viewer.get('overlays');

        // Run-state badges as on the editor's design board: done = ✓, failed = !.
        overlays.remove({ type: 'fl-run-state' });
        const badge = function (id, kind, text) {
            const el = document.createElement('span');
            el.className = 'orbit-badge orbit-badge--' + kind;
            el.textContent = text;
            overlays.add(id, 'fl-run-state', { position: { top: -8, right: 8 }, html: el });
        };

        elementRegistry.forEach(function (element) {
            canvas.removeMarker(element.id, 'bpmn-completed');
            canvas.removeMarker(element.id, 'bpmn-active');
            canvas.removeMarker(element.id, 'bpmn-error');
            canvas.removeMarker(element.id, 'bpmn-error-pulse');
            canvas.removeMarker(element.id, 'bpmn-cancelled');
        });

        if (completedIds) {
            completedIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-completed');
                    badge(id, 'done', '✓');
                }
            });
        }

        if (activeIds) {
            activeIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-active');
                    // The token dot the editor's simulation draws (orbitSimulation.js).
                    const token = document.createElement('span');
                    token.className = 'orbit-sim-token';
                    overlays.add(id, 'fl-run-state', { position: { top: -6, left: elementRegistry.get(id).width / 2 - 6 }, html: token });
                }
            });
        }

        if (errorIds) {
            errorIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, id === lastErrorId ? 'bpmn-error-pulse' : 'bpmn-error');
                    badge(id, 'error', '!');
                }
            });
        }

        if (cancelledIds) {
            cancelledIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-cancelled');
                }
            });
        }
    },

    // The path the run took, marked on the diagram's own sequence flows (as the editor's simulation
    // marks them, orbitSimulation.js) rather than drawn as a second line beside them.
    highlightExecutionPath: function (orderedActivityIds) {
        if (!this._viewer) return;

        var canvas = this._viewer.get('canvas');
        var elementRegistry = this._viewer.get('elementRegistry');

        // Build map: "sourceId→targetId" -> flow, clearing the previous path on the way.
        var flowMap = {};
        elementRegistry.forEach(function (el) {
            if (el.type !== 'bpmn:SequenceFlow') return;
            canvas.removeMarker(el.id, 'bpmn-flow-taken');
            if (el.businessObject.sourceRef && el.businessObject.targetRef) {
                flowMap[el.businessObject.sourceRef.id + '\u2192' + el.businessObject.targetRef.id] = el;
            }
        });

        if (!orderedActivityIds) return;

        // Each activity was entered over the flow from the latest earlier activity it connects from.
        for (var i = 1; i < orderedActivityIds.length; i++) {
            for (var j = i - 1; j >= 0; j--) {
                var flow = flowMap[orderedActivityIds[j] + '\u2192' + orderedActivityIds[i]];
                if (flow) {
                    canvas.addMarker(flow.id, 'bpmn-flow-taken');
                    break;
                }
            }
        }
    },

    clearSelection: function () {
        if (!this._viewer) return;

        var canvas = this._viewer.get('canvas');
        var elementRegistry = this._viewer.get('elementRegistry');
        elementRegistry.forEach(function (el) {
            canvas.removeMarker(el.id, 'bpmn-selected');
        });

        if (this._dotNetRef) {
            this._dotNetRef.invokeMethodAsync('OnBpmnElementClicked', '');
        }
    },

    selectElement: function (elementId) {
        if (!this._viewer) return;

        const canvas = this._viewer.get('canvas');
        const elementRegistry = this._viewer.get('elementRegistry');

        // Remove previous selection
        elementRegistry.forEach(function (el) {
            canvas.removeMarker(el.id, 'bpmn-selected');
        });

        const element = elementRegistry.get(elementId);
        if (!element) return;

        canvas.addMarker(elementId, 'bpmn-selected');

        // Center viewport on element
        var currentViewbox = canvas.viewbox();
        var elementMid = {
            x: element.x + (element.width || 0) / 2,
            y: element.y + (element.height || 0) / 2
        };

        canvas.viewbox({
            x: elementMid.x - currentViewbox.width / 2,
            y: elementMid.y - currentViewbox.height / 2,
            width: currentViewbox.width,
            height: currentViewbox.height
        });
    },

    // The container changed size (a side panel collapsed or expanded): keep the zoom, re-measure.
    resized: function () {
        if (!this._viewer) return;
        this._viewer.get('canvas').resized();
    },

    refit: function () {
        if (!this._viewer) return;
        var canvas = this._viewer.get('canvas');
        canvas.zoom('fit-viewport');
    },

    getElementProperties: function (elementId) {
        if (!this._viewer) return null;

        var elementRegistry = this._viewer.get('elementRegistry');
        var element = elementRegistry.get(elementId);
        if (!element) return null;

        // Reuse the editor's full extractor so the read-only instance panel
        // surfaces the same schema as the editor — task-definition type,
        // input/output mappings, expected outputs, user-task fields,
        // multi-instance loop characteristics, etc. The extractor only
        // touches `this._modeler.get('elementRegistry')`; bpmn-js Modeler
        // and Viewer expose the same registry surface, so binding `this`
        // to a stub with `_modeler` pointing at the viewer works.
        return window.bpmnEditor._extractElementData.call({ _modeler: this._viewer }, element);
    },

    // Read-only counterparts of bpmnEditor.getServiceTaskType / getIoMappings
    // for the instance page's properties panel (which has no modeler).
    getServiceTaskType: function (elementId) {
        if (!this._viewer) return null;
        return window.bpmnEditor.getServiceTaskType.call({ _modeler: this._viewer }, elementId);
    },

    getIoMappings: function (elementId) {
        if (!this._viewer) return [];
        return window.bpmnEditor.getIoMappings.call({ _modeler: this._viewer }, elementId);
    },

    destroy: function () {
        if (this._viewer) {
            this._viewer.destroy();
            this._viewer = null;
        }
        this._dotNetRef = null;
        this._clickHandlerRegistered = false;
    }
};
