window.bpmnViewer = {
    _viewer: null,
    _dotNetRef: null,
    _clickHandlerRegistered: false,

    // Selectable = any flow node (task, event, gateway, sub-process, ...) or sequence flow, so a newly
    // supported element type cannot silently lose its properties panel (a type list missed
    // ComplexGateway). A click on an element's label selects the element.
    _selectable: function (element) {
        var bo = element && element.businessObject;
        return !!bo && (bo.$instanceOf('bpmn:FlowNode') || bo.$instanceOf('bpmn:SequenceFlow'));
    },

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
            this.fit();
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
            var element = e.element.labelTarget || e.element;
            if (!self._selectable(element)) {
                self.clearSelection();
                return;
            }
            self.selectElement(element.id);
            if (self._dotNetRef) {
                self._dotNetRef.invokeMethodAsync('OnBpmnElementClicked', element.id);
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
    // steps: one per activity instance, { activityId, createdAt, doneAt } (ms; doneAt null unless it
    // completed successfully). A flow is taken when an instance of its source completed and an instance
    // of its target was created at or after that: the token left the source over it. Every flow into a
    // join is marked, not just the one from the latest branch.
    highlightExecutionPath: function (steps) {
        if (!this._viewer) return;

        var canvas = this._viewer.get('canvas');
        var elementRegistry = this._viewer.get('elementRegistry');

        // Earliest successful completion and latest creation per element.
        var firstDone = {};
        var lastCreated = {};
        (steps || []).forEach(function (s) {
            if (s.doneAt != null && !(firstDone[s.activityId] <= s.doneAt)) firstDone[s.activityId] = s.doneAt;
            if (s.createdAt != null && !(lastCreated[s.activityId] >= s.createdAt)) lastCreated[s.activityId] = s.createdAt;
        });

        elementRegistry.forEach(function (el) {
            if (el.type !== 'bpmn:SequenceFlow') return;
            canvas.removeMarker(el.id, 'bpmn-flow-taken');
            var bo = el.businessObject;
            if (!bo.sourceRef || !bo.targetRef) return;
            var done = firstDone[bo.sourceRef.id];
            var created = lastCreated[bo.targetRef.id];
            if (done != null && created != null && created >= done) {
                canvas.addMarker(el.id, 'bpmn-flow-taken');
            }
        });
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
    // Fits the diagram into the part of the canvas no floating bar covers (bars overlay the canvas,
    // as in the editor): at most 100%, centred. bpmn-js' own fit-viewport puts a diagram that fits at
    // the top left, under the bar.
    fit: function () {
        if (!this._viewer) return;
        var canvas = this._viewer.get('canvas');
        var container = canvas.getContainer();
        var box = container.getBoundingClientRect();
        if (!box.width || !box.height) return;

        var margin = 24;
        var top = margin;
        container.parentElement.querySelectorAll('.orbit-float').forEach(function (bar) {
            var r = bar.getBoundingClientRect();
            if (r.bottom > box.top && r.top < box.top + box.height / 2)
                top = Math.max(top, r.bottom - box.top + margin);
        });

        canvas.resized();
        var inner = canvas.viewbox().inner;
        if (!inner.width || !inner.height) return;
        var width = box.width - 2 * margin;
        var height = box.height - top - margin;
        var scale = Math.min(1, width / inner.width, height / inner.height);
        canvas.viewbox({
            x: inner.x - (margin + (width - inner.width * scale) / 2) / scale,
            y: inner.y - (top + (height - inner.height * scale) / 2) / scale,
            width: box.width / scale,
            height: box.height / scale
        });
    },

    resized: function () {
        if (!this._viewer) return;
        this._viewer.get('canvas').resized();
    },

    // Instance page status bar (as the editor's, bpmnEditor.bindChrome): keeps [data-orbit-zoom] and
    // [data-orbit-cursor] under rootId current from the viewbox and the pointer.
    bindStatus: function (rootId) {
        var root = document.getElementById(rootId);
        if (!root || !this._viewer) return;
        var canvas = this._viewer.get('canvas');

        function updateZoom() {
            var zoom = Math.round(canvas.zoom() * 100) + '%';
            root.querySelectorAll('[data-orbit-zoom]').forEach(function (label) { label.textContent = zoom; });
        }

        this._viewer.get('eventBus').on(['canvas.viewbox.changed', 'canvas.resized'], updateZoom);
        updateZoom();

        var container = canvas.getContainer();
        container.addEventListener('mousemove', function (event) {
            var label = root.querySelector('[data-orbit-cursor]');
            if (!label) return;
            var rect = container.getBoundingClientRect();
            var viewbox = canvas.viewbox();
            var x = Math.round(viewbox.x + (event.clientX - rect.left) / viewbox.scale);
            var y = Math.round(viewbox.y + (event.clientY - rect.top) / viewbox.scale);
            label.textContent = 'X ' + x + ' · Y ' + y;
        });
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
