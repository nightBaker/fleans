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

// Read input/output mappings from a zeebe:IoMapping or fleans:IoMapping container into data.
function _readIoMappingContainer(bo, data) {
    if (!bo.extensionElements || !bo.extensionElements.values) return;
    var vals = bo.extensionElements.values;
    for (var i = 0; i < vals.length; i++) {
        if (_isExt(vals[i], 'IoMapping')) {
            var container = vals[i];
            (container.inputs || []).forEach(function(m) {
                data.inputMappings.push({ source: m.source || '', target: m.target || '' });
            });
            (container.outputs || []).forEach(function(m) {
                data.outputMappings.push({ source: m.source || '', target: m.target || '' });
            });
            break;
        }
    }
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

        this._modeler = window.fleansBpmnTheme.create(BpmnModeler, {
            container: container,
            keyboard: { bindTo: container },
            moddleExtensions: Object.assign(
                {},
                window.fleansModdleExtension ? { fleans: window.fleansModdleExtension } : {},
                window.zeebeModdleExtension ? { zeebe: window.zeebeModdleExtension } : {}
            ),
            additionalModules: [window.boundaryEventContextPadModule, window.orbitContextMenuModule]
                .filter(function (m) { return !!m; })
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

            // Context-menu actions that change the selected element's properties outside the panel
            // (e.g. "Make default flow") re-send its data so the panel doesn't show stale values.
            eventBus.on('orbit.elementChanged', function (e) {
                dotNetRef.invokeMethodAsync('OnElementSelected', self._extractElementData(e.element));
            });

            eventBus.on('orbit.showXml', function () {
                dotNetRef.invokeMethodAsync('OnShowXmlRequested');
            });

            eventBus.on('orbit.openProperties', function (e) {
                self._focusProperties(e.field);
            });

            eventBus.on(['import.done', 'commandStack.changed'], function () {
                self._scheduleSummary();
            });

            eventBus.on('orbit.simulateFrom', function (e) {
                dotNetRef.invokeMethodAsync('OnSimulateFromRequested', e.element.id);
            });
        }

        this._installReadOnlyGuards();
    },

    // ---- Read-only (Simulation / History modes) ----------------------------------------------
    // bpmn-js has no read-only switch. Every interactive edit (move, create, connect, resize, paste,
    // delete) asks rules.allowed first, so this modeler's rules instance answers "no" while read-only.
    // (A 'commandStack.canExecute' listener wouldn't do: the command-specific rule listener answers
    // first and stops the bubble.) Label editing, undo/redo and the context menu/pad are blocked
    // separately. Markers are not commands, so simulation and diff highlights still work.
    _installReadOnlyGuards: function () {
        var self = this;
        var modeler = this._modeler;
        var eventBus = modeler.get('eventBus');
        var rules = modeler.get('rules');
        var allowed = rules.allowed.bind(rules);
        rules.allowed = function (action, context) {
            return self._readOnly ? false : allowed(action, context);
        };
        eventBus.on('element.dblclick', 3000, function () {
            if (self._readOnly) return false;
        });
        eventBus.on('contextPad.open', 3000, function () {
            if (self._readOnly) return false;
        });
        var keyboard = modeler.get('keyboard');
        keyboard.addListener(3000, function (context) {
            if (!self._readOnly) return;
            var e = context.keyEvent;
            // Swallow every editing key; navigation (arrows, +/-, Shift F10 → disabled menu) passes.
            if (keyboard.isCmd(e) || e.key === 'Delete' || e.key === 'Backspace' || e.key === 'F2' ||
                /^[a-zA-Z]$/.test(e.key)) {
                return true;
            }
        });
    },

    setReadOnly: function (readOnly) {
        if (!this._modeler) return;
        this._readOnly = !!readOnly;
        var container = this._modeler.get('canvas').getContainer();
        container.classList.toggle('orbit-readonly', this._readOnly);
        if (this._readOnly) {
            this._modeler.get('contextPad').close();
            this._modeler.get('directEditing').cancel();
            this._modeler.get('popupMenu').close();
        }
        var menu = this._modeler.get('orbitContextMenu', false);
        if (menu) menu.setEnabled(!this._readOnly);
    },

    // ---- Simulation mode (js/orbitSimulation.js) --------------------------------------------

    simulation: function (command, arg, arg2) {
        if (!this._modeler) return;
        var self = this;
        if (command === 'start') {
            if (this._simulation) this._simulation.destroy();
            this._simulation = new window.OrbitSimulation(this._modeler, function (snapshot) {
                if (self._dotNetRef) self._dotNetRef.invokeMethodAsync('OnSimulationUpdate', snapshot);
            });
            if (arg) this._simulation.startFrom(arg); else this._simulation.start();
            return;
        }
        var sim = this._simulation;
        if (!sim) return;
        switch (command) {
            case 'pause': sim.pause(); break;
            case 'resume': sim.resume(); break;
            case 'step': sim.step(); break;
            case 'reset': sim.reset(); break;
            case 'speed': sim.setSpeed(arg); break;
            case 'choose': sim.choose(arg, arg2); break;
            case 'stop':
                sim.destroy();
                this._simulation = null;
                break;
        }
    },

    // ---- History mode: compare the open diagram with a deployed version ----------------------

    // Diffs flow elements (and pools) by id. Returns a capped list for the inspector — the full
    // XML stays here — and marks the canvas: added = green "+", changed = amber "~". Removed
    // elements no longer exist on the canvas, so they are only listed.
    compareWith: async function (baseXml) {
        this.clearComparison();
        if (!this._modeler) return null;
        var moddle = this._modeler.get('moddle');
        var parsed = await moddle.fromXML(baseXml, 'bpmn:Definitions');
        var base = this._collectElements(parsed.rootElement);
        var current = this._collectElements(this._modeler.getDefinitions());
        var items = [];
        var counts = { added: 0, changed: 0, removed: 0 };

        Object.keys(current).forEach(function (id) {
            var now = current[id], before = base[id];
            if (!before) {
                counts.added++;
                items.push({ id: id, kind: 'added', name: now.name, type: now.type, fields: [] });
                return;
            }
            var fields = [];
            if (before.type !== now.type) fields.push({ field: 'type', oldValue: before.type, newValue: now.type });
            if (before.name !== now.name) fields.push({ field: 'name', oldValue: before.name.slice(0, 60), newValue: now.name.slice(0, 60) });
            Object.keys(Object.assign({}, before.props, now.props)).forEach(function (key) {
                var a = before.props[key] || '', b = now.props[key] || '';
                if (a !== b) fields.push({ field: key, oldValue: a.slice(0, 60), newValue: b.slice(0, 60) });
            });
            if (fields.length) {
                counts.changed++;
                items.push({ id: id, kind: 'changed', name: now.name, type: now.type, fields: fields.slice(0, 4) });
            }
        });
        Object.keys(base).forEach(function (id) {
            if (current[id]) return;
            counts.removed++;
            items.push({ id: id, kind: 'removed', name: base[id].name, type: base[id].type, fields: [] });
        });

        var canvas = this._modeler.get('canvas');
        var overlays = this._modeler.get('overlays');
        var registry = this._modeler.get('elementRegistry');
        var marked = [];
        items.forEach(function (item) {
            if (item.kind === 'removed' || !registry.get(item.id)) return;
            var marker = 'orbit-diff-' + item.kind;
            canvas.addMarker(item.id, marker);
            marked.push({ id: item.id, marker: marker });
            var badge = document.createElement('span');
            badge.className = 'orbit-badge orbit-badge--' + (item.kind === 'added' ? 'done' : 'warning');
            badge.textContent = item.kind === 'added' ? '+' : '~';
            var el = registry.get(item.id);
            overlays.add(item.id, 'orbit-diff', { position: el.waypoints ? { top: -8, left: -8 } : { top: -8, right: 8 }, html: badge });
        });
        this._diffMarked = marked;

        var order = { removed: 0, changed: 1, added: 2 };
        items.sort(function (a, b) { return order[a.kind] - order[b.kind]; });
        items.forEach(function (i) { i.name = (i.name || '').slice(0, 60); });
        // Capped (40 items × 4 fields × 60 chars) to stay under the SignalR receive limit.
        return { added: counts.added, changed: counts.changed, removed: counts.removed, items: items.slice(0, 40) };
    },

    clearComparison: function () {
        if (!this._modeler) return;
        var canvas = this._modeler.get('canvas');
        var registry = this._modeler.get('elementRegistry');
        (this._diffMarked || []).forEach(function (m) {
            if (registry.get(m.id)) canvas.removeMarker(m.id, m.marker);
        });
        this._diffMarked = null;
        this._modeler.get('overlays').remove({ type: 'orbit-diff' });
    },

    _collectElements: function (definitions) {
        var map = {};
        function text(expr) { return expr && (expr.body || expr.value) ? String(expr.body || expr.value) : ''; }
        function summarize(el) {
            var props = {};
            if (el.conditionExpression) props.condition = text(el.conditionExpression);
            if (el.script) props.script = el.script;
            if (el.calledElement) props.calledElement = el.calledElement;
            if (el['default']) props['default flow'] = el['default'].id;
            if (el.sourceRef && el.sourceRef.id) props.source = el.sourceRef.id;
            if (el.targetRef && el.targetRef.id) props.target = el.targetRef.id;
            if (el.attachedToRef) props.attachedTo = el.attachedToRef.id;
            var def = el.eventDefinitions && el.eventDefinitions[0];
            if (def) {
                props.event = def.$type.replace(/^bpmn:|EventDefinition$/g, '');
                var timer = text(def.timeDuration) || text(def.timeDate) || text(def.timeCycle);
                if (timer) props.timer = timer;
                if (def.messageRef) props.message = def.messageRef.name || def.messageRef.id;
                if (def.signalRef) props.signal = def.signalRef.name || def.signalRef.id;
                if (def.errorRef) props.error = def.errorRef.errorCode || def.errorRef.id;
            }
            if (el.loopCharacteristics) props.multiInstance = el.loopCharacteristics.isSequential ? 'sequential' : 'parallel';
            return { type: el.$type, name: el.name || '', props: props };
        }
        function walk(container) {
            (container.flowElements || []).forEach(function (el) {
                map[el.id] = summarize(el);
                if (el.flowElements) walk(el);
            });
        }
        (definitions.rootElements || []).forEach(function (root) {
            if (root.$type === 'bpmn:Process') walk(root);
            if (root.$type === 'bpmn:Collaboration') {
                (root.participants || []).forEach(function (p) { map[p.id] = { type: p.$type, name: p.name || '', props: {} }; });
            }
        });
        return map;
    },

    // Empty-state "Add start event": places a catalog element in the middle of the view.
    addElementAtCenter: function (key) {
        if (!this._modeler) return;
        var canvas = this._modeler.get('canvas');
        var shape = window.orbitElementCatalog.createShape(this._modeler.get('elementFactory'), key);
        if (!shape) return;
        var vb = canvas.viewbox();
        var created = this._modeler.get('modeling').createShape(shape,
            { x: Math.round(vb.x + vb.width / 2), y: Math.round(vb.y + vb.height / 2) }, canvas.getRootElement());
        this._modeler.get('selection').select(created);
    },

    // ---- Orbit editor chrome (Editor.razor) -------------------------------------------------
    // The left-rail palette, the floating tool/zoom bars and the status bar are Razor markup; this
    // wires them to bpmn-js by data attributes, because palette drags and tool activation need the
    // native DOM event (bpmn-js starts its drag session from it) which a Blazor @onclick can't pass.
    //   [data-bpmn-create="<catalog key>"]  click or drag → create that element (orbitContextMenu.js catalog)
    //   [data-bpmn-tool="<name>"]            select | hand | lasso | space | connect | undo | redo |
    //                                        zoom-in | zoom-out | fit
    //   [data-orbit-zoom] / [data-orbit-cursor]  text kept current from the viewbox / pointer
    bindChrome: function (rootId) {
        var root = document.getElementById(rootId);
        if (!root || !this._modeler || root._orbitBound) return;
        root._orbitBound = true;

        var self = this;
        var modeler = this._modeler;
        var eventBus = modeler.get('eventBus');
        var canvas = modeler.get('canvas');

        function startCreate(target, event) {
            var shape = window.orbitElementCatalog.createShape(modeler.get('elementFactory'), target.getAttribute('data-bpmn-create'));
            if (shape) modeler.get('create').start(event, shape);
        }

        root.addEventListener('dragstart', function (event) {
            var target = event.target.closest && event.target.closest('[data-bpmn-create]');
            if (!target) return;
            // Cancel the native HTML drag; bpmn-js' own drag session follows the pointer instead.
            event.preventDefault();
            startCreate(target, event);
        });

        root.addEventListener('click', function (event) {
            var create = event.target.closest('[data-bpmn-create]');
            if (create) {
                startCreate(create, event);
                return;
            }
            var tool = event.target.closest('[data-bpmn-tool]');
            if (tool) self._runTool(tool.getAttribute('data-bpmn-tool'), event);
        });

        function setPressed(name) {
            root.querySelectorAll('[data-bpmn-tool][aria-pressed]').forEach(function (b) {
                b.setAttribute('aria-pressed', b.getAttribute('data-bpmn-tool') === name ? 'true' : 'false');
            });
        }

        var TOOL_NAMES = { 'hand': 'hand', 'lasso': 'lasso', 'space': 'space', 'global-connect': 'connect' };
        eventBus.on('tool-manager.update', function (e) {
            setPressed(TOOL_NAMES[e.tool] || 'select');
        });

        function updateHistory() {
            var stack = modeler.get('commandStack');
            var undo = root.querySelector('[data-bpmn-tool="undo"]');
            var redo = root.querySelector('[data-bpmn-tool="redo"]');
            if (undo) undo.disabled = !stack.canUndo();
            if (redo) redo.disabled = !stack.canRedo();
        }

        function updateZoom() {
            var zoom = Math.round(canvas.zoom() * 100) + '%';
            root.querySelectorAll('[data-orbit-zoom]').forEach(function (label) { label.textContent = zoom; });
        }

        eventBus.on(['commandStack.changed', 'import.done'], updateHistory);
        eventBus.on(['canvas.viewbox.changed', 'import.done', 'canvas.resized'], updateZoom);
        updateHistory();
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

        this._bindMinimap(root);
    },

    // Minimap (design: bottom-right card): shapes and flows drawn at diagram scale into an SVG whose
    // viewBox is the diagram's bounds, plus the current viewport. Click or drag to move the view.
    _bindMinimap: function (root) {
        var host = root.querySelector('[data-orbit-minimap]');
        if (!host) return;
        var modeler = this._modeler;
        var canvas = modeler.get('canvas');
        var registry = modeler.get('elementRegistry');
        var eventBus = modeler.get('eventBus');
        var SVG = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(SVG, 'svg');
        svg.setAttribute('class', 'orbit-minimap__svg');
        svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
        svg.setAttribute('aria-hidden', 'true');
        host.appendChild(svg);
        var viewport = null;
        var drawTimer = null;

        function el(name, attrs) {
            var node = document.createElementNS(SVG, name);
            Object.keys(attrs).forEach(function (k) { node.setAttribute(k, attrs[k]); });
            return node;
        }

        function draw() {
            while (svg.firstChild) svg.removeChild(svg.firstChild);
            var rootEl = canvas.getRootElement();
            var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
            var shapes = [], flows = [];
            registry.forEach(function (e) {
                if (e === rootEl || e.labelTarget || !e.businessObject) return;
                if (e.waypoints) { flows.push(e); return; }
                shapes.push(e);
                minX = Math.min(minX, e.x); minY = Math.min(minY, e.y);
                maxX = Math.max(maxX, e.x + e.width); maxY = Math.max(maxY, e.y + e.height);
            });
            if (!shapes.length) { minX = 0; minY = 0; maxX = 400; maxY = 200; }
            var pad = 40;
            svg.setAttribute('viewBox', [minX - pad, minY - pad, maxX - minX + 2 * pad, maxY - minY + 2 * pad].join(' '));
            flows.forEach(function (f) {
                svg.appendChild(el('polyline', {
                    points: f.waypoints.map(function (p) { return p.x + ',' + p.y; }).join(' '),
                    'class': 'orbit-minimap__flow'
                }));
            });
            shapes.forEach(function (s) {
                var t = s.businessObject.$type;
                var kind = t === 'bpmn:StartEvent' ? 'start' : t === 'bpmn:EndEvent' ? 'end'
                    : /Gateway$/.test(t) ? 'gateway' : /Event$/.test(t) ? 'event'
                    : (t === 'bpmn:Participant' || t === 'bpmn:Lane' || (t === 'bpmn:SubProcess' && !s.collapsed)) ? 'container' : 'task';
                if (kind === 'start' || kind === 'end' || kind === 'event') {
                    svg.appendChild(el('circle', { cx: s.x + s.width / 2, cy: s.y + s.height / 2, r: s.width / 2, 'class': 'orbit-minimap__shape orbit-minimap__shape--' + kind }));
                } else if (kind === 'gateway') {
                    var cx = s.x + s.width / 2, cy = s.y + s.height / 2;
                    svg.appendChild(el('polygon', { points: [cx, s.y, s.x + s.width, cy, cx, s.y + s.height, s.x, cy].join(' '), 'class': 'orbit-minimap__shape orbit-minimap__shape--gateway' }));
                } else {
                    svg.appendChild(el('rect', { x: s.x, y: s.y, width: s.width, height: s.height, rx: 8, 'class': 'orbit-minimap__shape orbit-minimap__shape--' + kind }));
                }
            });
            viewport = el('rect', { 'class': 'orbit-minimap__viewport', rx: 6 });
            svg.appendChild(viewport);
            moveViewport();
        }

        function moveViewport() {
            if (!viewport) return;
            var vb = canvas.viewbox();
            viewport.setAttribute('x', vb.x);
            viewport.setAttribute('y', vb.y);
            viewport.setAttribute('width', Math.max(vb.width, 1));
            viewport.setAttribute('height', Math.max(vb.height, 1));
        }

        function scheduleDraw() {
            clearTimeout(drawTimer);
            drawTimer = setTimeout(draw, 120);
        }

        function centerOn(event) {
            var ctm = svg.getScreenCTM();
            if (!ctm) return;
            var pt = svg.createSVGPoint();
            pt.x = event.clientX; pt.y = event.clientY;
            var p = pt.matrixTransform(ctm.inverse());
            var vb = canvas.viewbox();
            canvas.viewbox({ x: p.x - vb.width / 2, y: p.y - vb.height / 2, width: vb.width, height: vb.height });
        }

        var dragging = false;
        svg.addEventListener('pointerdown', function (e) { dragging = true; svg.setPointerCapture(e.pointerId); centerOn(e); });
        svg.addEventListener('pointermove', function (e) { if (dragging) centerOn(e); });
        svg.addEventListener('pointerup', function () { dragging = false; });

        eventBus.on(['import.done', 'elements.changed'], scheduleDraw);
        eventBus.on(['canvas.viewbox.changed', 'canvas.resized'], moveViewport);
        draw();
    },

    _runTool: function (name, event) {
        var modeler = this._modeler;
        if (!modeler) return;
        var canvas = modeler.get('canvas');
        switch (name) {
            case 'select': modeler.get('dragging').cancel(); break;
            case 'hand': modeler.get('handTool').activateHand(event); break;
            case 'lasso': modeler.get('lassoTool').activateSelection(event); break;
            case 'space': modeler.get('spaceTool').activateSelection(event); break;
            case 'connect': modeler.get('globalConnect').start(event); break;
            case 'undo': modeler.get('commandStack').undo(); break;
            case 'redo': modeler.get('commandStack').redo(); break;
            case 'zoom-in': modeler.get('zoomScroll').stepZoom(1); break;
            case 'zoom-out': modeler.get('zoomScroll').stepZoom(-1); break;
            case 'fit': canvas.zoom('fit-viewport', 'auto'); break;
        }
    },

    // Called by the editor after it shows the canvas again (XML mode hides it).
    refreshCanvas: function () {
        if (!this._modeler) return;
        var canvas = this._modeler.get('canvas');
        canvas.resized();
        if (this._fitPending) {
            this._fitPending = false;
            canvas.zoom('fit-viewport');
        }
    },

    // Selects an element and scrolls it into view (outline, problems list).
    focusElement: function (elementId) {
        if (!this._modeler) return false;
        var element = this._modeler.get('elementRegistry').get(elementId);
        if (!element) return false;
        var canvas = this._modeler.get('canvas');
        if (element !== canvas.getRootElement()) {
            this._modeler.get('selection').select(element);
            canvas.scrollToElement(element, { top: 120, bottom: 120, left: 160, right: 160 });
        }
        // Keyboard bindings listen on the canvas container: move focus there so Delete, F2, … apply.
        var binding = this._modeler.get('keyboard').getBinding();
        if (binding) binding.focus({ preventScroll: true });
        return true;
    },

    // "Open properties" / "Set condition…": focus the first field of the inspector, or the one tagged
    // data-field="<field>". Blazor renders the panel after the selection round-trip, hence the retries.
    _focusProperties: function (field) {
        var attempts = 0;
        (function tryFocus() {
            var panel = document.querySelector('.properties-panel');
            var target = panel && (field
                ? panel.querySelector('[data-field="' + field + '"]')
                : panel.querySelector('fluent-text-field, fluent-text-area, input, textarea'));
            if (target) {
                target.scrollIntoView({ block: 'nearest' });
                target.focus();
            } else if (attempts++ < 10) {
                setTimeout(tryFocus, 50);
            }
        })();
    },

    // ---- XML mode -----------------------------------------------------------------------------
    // The XML text lives only in the browser (the <textarea> Editor.razor renders without a bound value):
    // a diagram's XML can exceed the SignalR message limit, so it never round-trips through .NET here.
    // .NET only hears that the text was edited (OnXmlEdited) and whether applying it worked.

    enterXmlMode: async function (textareaId) {
        var area = document.getElementById(textareaId);
        if (!area || !this._modeler) return;
        var result = await this._modeler.saveXML({ format: true });
        area.value = result.xml;
        this._xmlBaseline = result.xml;
        this._xmlEdited = false;
        if (!area._orbitBound) {
            area._orbitBound = true;
            var self = this;
            area.addEventListener('input', function () {
                var edited = area.value !== self._xmlBaseline;
                if (edited !== self._xmlEdited) {
                    self._xmlEdited = edited;
                    if (self._dotNetRef) self._dotNetRef.invokeMethodAsync('OnXmlEdited', edited);
                }
            });
        }
        area.focus();
    },

    // Imports the edited XML. Call it with the canvas visible (loadXml fits the viewport).
    applyXmlFromEditor: async function (textareaId) {
        var area = document.getElementById(textareaId);
        if (!area) return { ok: false, error: 'XML editor not found.' };
        try {
            await this.loadXml(area.value);
            this._xmlBaseline = area.value;
            this._xmlEdited = false;
            return { ok: true, error: null };
        } catch (err) {
            return { ok: false, error: (err && err.message) || String(err) };
        }
    },

    copyXmlFromEditor: function (textareaId) {
        var area = document.getElementById(textareaId);
        return area ? window.fleansOrbit.copyText(area.value) : false;
    },

    downloadXmlFromEditor: function (textareaId, filename) {
        var area = document.getElementById(textareaId);
        if (!area) return;
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([area.value], { type: 'application/xml' }));
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 0);
    },

    // ---- Diagram summary: outline + lint (left rail, status bar, deploy checklist) --------------

    _scheduleSummary: function () {
        var self = this;
        clearTimeout(this._summaryTimer);
        this._summaryTimer = setTimeout(function () {
            if (!self._modeler || !self._dotNetRef) return;
            var summary = self.getSummary();
            self._dotNetRef.invokeMethodAsync('OnDiagramSummary', summary);
        }, 200);
    },

    getSummary: function () {
        if (!this._modeler) return null;
        var registry = this._modeler.get('elementRegistry');
        var canvas = this._modeler.get('canvas');
        var elements = 0, flows = 0;
        registry.forEach(function (e) {
            if (e.labelTarget || !e.businessObject || e === canvas.getRootElement()) return;
            var type = e.businessObject.$type;
            if (type === 'bpmn:SequenceFlow') flows++;
            else if (e.businessObject.$instanceOf('bpmn:FlowNode')) elements++;
        });
        var problems = this._lint();
        this._markProblems(problems);
        // Capped so the payload stays well under the SignalR message limit (32 KB by default).
        return {
            elements: elements,
            flows: flows,
            outline: this._outline().slice(0, 120),
            problems: problems.slice(0, 60),
            problemCounts: {
                errors: problems.filter(function (p) { return p.severity === 'error'; }).length,
                warnings: problems.filter(function (p) { return p.severity === 'warning'; }).length,
                infos: problems.filter(function (p) { return p.severity === 'info'; }).length
            }
        };
    },

    _outline: function () {
        var canvas = this._modeler.get('canvas');
        var items = [];
        function kind(bo) {
            var t = bo.$type;
            if (t === 'bpmn:StartEvent') return 'start';
            if (t === 'bpmn:EndEvent') return 'end';
            if (/Gateway$/.test(t)) return 'gateway';
            if (/Event$/.test(t)) return 'event';
            return 'task';
        }
        function walk(container, depth) {
            var children = (container.children || []).filter(function (c) {
                return !c.labelTarget && !c.waypoints && c.businessObject;
            });
            children.sort(function (a, b) { return (a.x - b.x) || (a.y - b.y); });
            children.forEach(function (child) {
                var bo = child.businessObject;
                if (bo.$instanceOf('bpmn:FlowNode') && items.length < 120) {
                    items.push({ id: bo.id, name: (bo.name || '').slice(0, 60), type: bo.$type, kind: kind(bo), depth: depth });
                    if (child.children && child.children.length) walk(child, depth + 1);
                } else if (bo.$type === 'bpmn:Participant' || bo.$type === 'bpmn:Lane') {
                    walk(child, depth);
                }
            });
        }
        walk(canvas.getRootElement(), 0);
        return items;
    },

    // Modelling hints, not the engine's validation (deploy still runs the real BPMN conversion):
    // errors = the engine can't run it as drawn, warnings = it probably won't do what you meant.
    _lint: function () {
        var registry = this._modeler.get('elementRegistry');
        var canvas = this._modeler.get('canvas');
        var root = canvas.getRootElement();
        var problems = [];
        function add(bo, severity, message) {
            problems.push({ id: bo.id, name: (bo.name || '').slice(0, 60), type: bo.$type, severity: severity, message: message });
        }
        function definition(bo) {
            return (bo.eventDefinitions && bo.eventDefinitions[0]) || null;
        }
        // The diagram's connections, not bo.incoming/outgoing: those are only filled when the XML
        // carries <bpmn:incoming>/<bpmn:outgoing> refs, which many exporters omit.
        function sequenceFlows(connections) {
            return (connections || [])
                .map(function (c) { return c.businessObject; })
                .filter(function (f) { return f && f.$type === 'bpmn:SequenceFlow'; });
        }

        var hasStart = false, hasEnd = false;
        registry.forEach(function (e) {
            if (e.labelTarget || e === root || !e.businessObject) return;
            var bo = e.businessObject;
            if (!bo.$instanceOf('bpmn:FlowNode')) return;
            var type = bo.$type;
            var parentBo = bo.$parent;
            var inEventSubProcess = parentBo && parentBo.triggeredByEvent;
            var topLevel = parentBo && parentBo.$type === 'bpmn:Process';
            if (topLevel && type === 'bpmn:StartEvent') hasStart = true;
            if (topLevel && type === 'bpmn:EndEvent') hasEnd = true;

            var incoming = sequenceFlows(e.incoming);
            var outgoing = sequenceFlows(e.outgoing);
            var exempt = bo.isForCompensation || bo.triggeredByEvent;
            var def = definition(bo);

            if (!exempt && incoming.length === 0 && type !== 'bpmn:StartEvent' && type !== 'bpmn:BoundaryEvent') {
                add(bo, 'warning', 'No incoming flow: this step is never reached.');
            }
            var compensateBoundary = type === 'bpmn:BoundaryEvent' && def && def.$type === 'bpmn:CompensateEventDefinition';
            if (!exempt && !compensateBoundary && outgoing.length === 0 && type !== 'bpmn:EndEvent' && !inEventSubProcess) {
                add(bo, 'warning', 'No outgoing flow: the path stops here without an end event.');
            }

            if ((type === 'bpmn:ExclusiveGateway' || type === 'bpmn:InclusiveGateway') && outgoing.length > 1) {
                outgoing.forEach(function (flow) {
                    if (flow !== bo['default'] && !flow.conditionExpression) {
                        add(flow, 'error', 'Branch of "' + (bo.name || bo.id) + '" has no condition and is not the default flow.');
                    }
                });
            }

            if (def) {
                if (def.$type === 'bpmn:TimerEventDefinition' && !def.timeDuration && !def.timeDate && !def.timeCycle) {
                    add(bo, 'error', 'Timer has no date, duration or cycle.');
                }
                if (def.$type === 'bpmn:MessageEventDefinition' && !def.messageRef) {
                    add(bo, 'error', 'Message event has no message name.');
                }
                if (def.$type === 'bpmn:SignalEventDefinition' && !def.signalRef) {
                    add(bo, 'error', 'Signal event has no signal name.');
                }
            }

            if (type === 'bpmn:CallActivity' && !bo.calledElement) {
                add(bo, 'error', 'Call activity has no called process key.');
            }
            if (type === 'bpmn:ScriptTask' && !bo.script) {
                add(bo, 'warning', 'Script task has an empty script.');
            }
            if (/Task$|^bpmn:CallActivity$/.test(type) && !bo.name) {
                add(bo, 'info', 'Task has no name.');
            }
        });

        var pools = 0;
        registry.forEach(function (e) {
            if (e.labelTarget || !e.businessObject) return;
            var bo = e.businessObject;
            if (bo.$type === 'bpmn:Participant') pools++;
            if (bo.$type === 'bpmn:DataObjectReference' || bo.$type === 'bpmn:DataStoreReference') {
                add(bo, 'info', 'Data objects and stores are documentation only: the engine ignores them.');
            }
        });
        if (pools > 1) {
            add(root.businessObject, 'warning', 'Only the first pool\'s process is deployed; the other pools are ignored.');
        }

        var rootBo = root.businessObject;
        if (rootBo && (rootBo.$type === 'bpmn:Process' || rootBo.$type === 'bpmn:Collaboration')) {
            if (!hasStart) add(rootBo, 'error', 'Process has no start event.');
            if (!hasEnd) add(rootBo, 'warning', 'Process has no end event.');
        }

        var order = { error: 0, warning: 1, info: 2 };
        problems.sort(function (a, b) { return order[a.severity] - order[b.severity]; });
        return problems;
    },

    _markProblems: function (problems) {
        var canvas = this._modeler.get('canvas');
        var overlays = this._modeler.get('overlays');
        var registry = this._modeler.get('elementRegistry');
        (this._markedProblems || []).forEach(function (m) {
            if (registry.get(m.id)) canvas.removeMarker(m.id, m.marker);
        });
        overlays.remove({ type: 'orbit-problem' });

        var worst = {};
        problems.forEach(function (p) {
            if (p.severity === 'info') return;
            if (!worst[p.id] || p.severity === 'error') worst[p.id] = p.severity;
        });

        var marked = [];
        var root = canvas.getRootElement();
        Object.keys(worst).forEach(function (id) {
            var element = registry.get(id);
            if (!element || element === root) return;
            var marker = 'orbit-problem-' + worst[id];
            canvas.addMarker(id, marker);
            marked.push({ id: id, marker: marker });
            var badge = document.createElement('span');
            badge.className = 'orbit-badge orbit-badge--' + worst[id];
            badge.textContent = worst[id] === 'error' ? '!' : '?';
            var position = element.waypoints ? { top: -8, left: -8 } : { top: -8, right: 8 };
            overlays.add(id, 'orbit-problem', { position: position, html: badge });
        });
        this._markedProblems = marked;
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
            hasTerminateDefinition: false,
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
            eventDefinitions: [],
            isExecutable: false,
            documentation: '',
            isEventSubProcess: false,
            hasConditionalDefinition: false,
            completionCondition: '',
            hasEscalationDefinition: false,
            escalationCode: '',
            activationCondition: '',
            defaultFlow: '',
            outgoingFlows: [],
            hasCompensateDefinition: false,
            compensateActivityRef: '',
            compensateWaitForCompletion: true,
            compensateAttachedTo: '',
            compensateActivities: []
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
            _readIoMappingContainer(bo, data);
        }

        if (bo.$type === 'bpmn:ServiceTask') {
            _readIoMappingContainer(bo, data);
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
                var def = bo.eventDefinitions[i];
                var entry = { index: i, kind: '' };

                if (def.$type === 'bpmn:TimerEventDefinition') {
                    entry.kind = 'Timer';
                    if (def.timeDuration)   { entry.timerType = 'duration'; entry.timerExpression = def.timeDuration.body || ''; }
                    else if (def.timeDate)  { entry.timerType = 'date';     entry.timerExpression = def.timeDate.body || ''; }
                    else if (def.timeCycle) { entry.timerType = 'cycle';    entry.timerExpression = def.timeCycle.body || ''; }
                    else                    { entry.timerType = 'duration'; entry.timerExpression = ''; }
                } else if (def.$type === 'bpmn:MessageEventDefinition') {
                    entry.kind = 'Message';
                    entry.messageName = def.messageRef ? (def.messageRef.name || '') : '';
                    entry.correlationKey = '';
                    // Priority 1: fleans:Subscription or zeebe:Subscription in the message's extension elements.
                    // Strip the FEEL expression prefix ("= varName" → "varName") — Fleans normalizes
                    // to plain variable names; BpmnConverter.ParseMessages already does TrimStart('=', ' ').
                    if (def.messageRef && def.messageRef.extensionElements) {
                        var extVals = def.messageRef.extensionElements.values || [];
                        for (var j = 0; j < extVals.length; j++) {
                            if (_isExt(extVals[j], 'Subscription')) {
                                entry.correlationKey = (extVals[j].correlationKey || '').replace(/^=\s*/, '');
                                break;
                            }
                        }
                    }
                    // Priority 2: legacy fleans:correlationKey / zeebe:correlationKey attribute on the event element.
                    if (!entry.correlationKey) entry.correlationKey = _extAttr(bo.$attrs, 'correlationKey') || '';
                } else if (def.$type === 'bpmn:SignalEventDefinition') {
                    entry.kind = 'Signal';
                    entry.signalName = def.signalRef ? (def.signalRef.name || '') : '';
                } else if (def.$type === 'bpmn:ErrorEventDefinition') {
                    entry.kind = 'Error';
                    entry.errorCode = def.errorRef ? (def.errorRef.errorCode || '') : '';
                } else if (def.$type === 'bpmn:CancelEventDefinition') {
                    entry.kind = 'Cancel';
                } else if (def.$type === 'bpmn:ConditionalEventDefinition') {
                    entry.kind = 'Conditional';
                    entry.conditionExpression = (def.condition && def.condition.body) ? def.condition.body : '';
                } else if (def.$type === 'bpmn:EscalationEventDefinition') {
                    entry.kind = 'Escalation';
                    entry.escalationCode = def.escalationRef ? (def.escalationRef.escalationCode || '') : '';
                } else if (def.$type === 'bpmn:TerminateEventDefinition') {
                    entry.kind = 'Terminate';
                } else if (def.$type === 'bpmn:CompensateEventDefinition') {
                    entry.kind = 'Compensate';
                    entry.activityRef = def.activityRef ? def.activityRef.id : '';
                    entry.waitForCompletion = def.waitForCompletion !== false;
                } else {
                    entry.kind = def.$type.replace('bpmn:', '').replace('EventDefinition', '');
                }

                data.eventDefinitions.push(entry);
            }

            // Populate ALL flat backward-compatible aliases by scanning every entry.
            // Last-write-wins for same-type multi-event (known limitation).
            data.eventDefinitions.forEach(function(entry) {
                if (entry.kind === 'Timer')       { data.hasTimerDefinition = true; data.timerType = entry.timerType; data.timerExpression = entry.timerExpression; }
                if (entry.kind === 'Message')     { data.hasMessageDefinition = true; data.messageName = entry.messageName; data.correlationKey = entry.correlationKey; }
                if (entry.kind === 'Signal')      { data.hasSignalDefinition = true; data.signalName = entry.signalName; }
                if (entry.kind === 'Error')       { data.hasErrorDefinition = true; data.errorCode = entry.errorCode; }
                if (entry.kind === 'Cancel')      { data.hasCancelDefinition = true; }
                if (entry.kind === 'Conditional') { data.hasConditionalDefinition = true; data.conditionExpression = entry.conditionExpression; }
                if (entry.kind === 'Escalation')  { data.hasEscalationDefinition = true; data.escalationCode = entry.escalationCode; }
                if (entry.kind === 'Terminate')   { data.hasTerminateDefinition = true; }
                if (entry.kind === 'Compensate')  { data.hasCompensateDefinition = true; data.compensateActivityRef = entry.activityRef; data.compensateWaitForCompletion = entry.waitForCompletion; }
            });
        }

        if (data.hasCompensateDefinition) {
            if (bo.$type === 'bpmn:BoundaryEvent' && bo.attachedToRef) {
                data.compensateAttachedTo = bo.attachedToRef.name || bo.attachedToRef.id || '';
            }
            if (bo.$type === 'bpmn:IntermediateThrowEvent' && this._modeler) {
                var compReg = this._modeler.get('elementRegistry');
                var COMPENSABLE = ['bpmn:Task', 'bpmn:UserTask', 'bpmn:ServiceTask', 'bpmn:ScriptTask',
                    'bpmn:ManualTask', 'bpmn:SendTask', 'bpmn:ReceiveTask', 'bpmn:BusinessRuleTask',
                    'bpmn:SubProcess', 'bpmn:CallActivity', 'bpmn:Transaction'];
                compReg.forEach(function (el) {
                    if (COMPENSABLE.indexOf(el.businessObject.$type) !== -1) {
                        data.compensateActivities.push({ id: el.id, name: el.businessObject.name || el.id });
                    }
                });
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

            if (miElement.completionCondition && miElement.completionCondition.body) {
                data.completionCondition = miElement.completionCondition.body;
            }
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

        if (bo.$type === 'bpmn:ComplexGateway' && bo.activationCondition) {
            data.activationCondition = bo.activationCondition.body || '';
        }

        if (bo.$type === 'bpmn:ExclusiveGateway' ||
            bo.$type === 'bpmn:InclusiveGateway' ||
            bo.$type === 'bpmn:ComplexGateway') {
            data.defaultFlow = bo.default ? bo.default.id : '';
            if (bo.outgoing) {
                bo.outgoing.forEach(function (flow) {
                    data.outgoingFlows.push({ id: flow.id, name: flow.name || '' });
                });
            }
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

    updateConditionalDefinition: function (elementId, expression) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;
        var condDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:ConditionalEventDefinition') {
                condDef = bo.eventDefinitions[i]; break;
            }
        }
        if (!condDef) return;
        if (expression && expression.trim() !== '') {
            var formalExpr = moddle.create('bpmn:FormalExpression', { body: expression });
            formalExpr.$parent = condDef;
            modeling.updateModdleProperties(element, condDef, { condition: formalExpr });
        } else {
            modeling.updateModdleProperties(element, condDef, { condition: undefined });
        }
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

    updateEscalationDefinition: function (elementId, escalationCode) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;

        var bo = element.businessObject;
        if (!bo.eventDefinitions || bo.eventDefinitions.length === 0) return;

        var escDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:EscalationEventDefinition') {
                escDef = bo.eventDefinitions[i];
                break;
            }
        }
        if (!escDef) return;

        var canvas = this._modeler.get('canvas');
        var definitions = canvas.getRootElement().businessObject.$parent;

        if (escalationCode) {
            var escEl = escDef.escalationRef;

            if (!escEl) {
                var escId = 'Escalation_' + elementId;
                escEl = moddle.create('bpmn:Escalation', { id: escId, escalationCode: escalationCode });
                escEl.$parent = definitions;

                var rootElements = definitions.get('rootElements');
                rootElements.push(escEl);

                moddle.ids.claim(escId, escEl);
            } else {
                escEl.escalationCode = escalationCode;
            }

            modeling.updateModdleProperties(element, escDef, { escalationRef: escEl });
        } else {
            modeling.updateModdleProperties(element, escDef, { escalationRef: undefined });
        }
    },

    updateActivationCondition: function (elementId, expression) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var moddle = this._modeler.get('moddle');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;
        if (bo.$type !== 'bpmn:ComplexGateway') return;
        if (expression) {
            var expr = moddle.create('bpmn:FormalExpression', { body: expression });
            modeling.updateProperties(element, { activationCondition: expr });
        } else {
            modeling.updateProperties(element, { activationCondition: undefined });
        }
    },

    updateDefaultFlow: function (elementId, flowId) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        if (flowId) {
            var flowEl = elementRegistry.get(flowId);
            modeling.updateProperties(element, { default: flowEl ? flowEl.businessObject : undefined });
        } else {
            modeling.updateProperties(element, { default: undefined });
        }
    },

    updateCompensateDefinition: function (elementId, activityRefId, waitForCompletion) {
        if (!this._modeler) return;
        var elementRegistry = this._modeler.get('elementRegistry');
        var modeling = this._modeler.get('modeling');
        var element = elementRegistry.get(elementId);
        if (!element) return;
        var bo = element.businessObject;
        if (!bo.eventDefinitions || !bo.eventDefinitions.length) return;
        var compDef = null;
        for (var i = 0; i < bo.eventDefinitions.length; i++) {
            if (bo.eventDefinitions[i].$type === 'bpmn:CompensateEventDefinition') {
                compDef = bo.eventDefinitions[i]; break;
            }
        }
        if (!compDef) return;
        var props = { waitForCompletion: !!waitForCompletion };
        if (activityRefId) {
            var targetEl = elementRegistry.get(activityRefId);
            props.activityRef = targetEl ? targetEl.businessObject : undefined;
        } else {
            props.activityRef = undefined;
        }
        modeling.updateModdleProperties(element, compDef, props);
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

            case 'completionCondition': {
                if (value && value.trim() !== '') {
                    var ccExpr = moddle.create('bpmn:FormalExpression', { body: value });
                    ccExpr.$parent = mi;
                    modeling.updateModdleProperties(element, mi, { completionCondition: ccExpr });
                } else {
                    modeling.updateModdleProperties(element, mi, { completionCondition: undefined });
                }
                break;
            }

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
            var container = canvas.getContainer();
            // XML mode hides the canvas; fitting a 0×0 viewport breaks the viewbox, so defer it.
            if (container.offsetWidth && container.offsetHeight) {
                canvas.zoom('fit-viewport');
            } else {
                this._fitPending = true;
            }
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
        clearTimeout(this._summaryTimer);
        if (this._simulation) this._simulation.destroy();
        this._simulation = null;
        this._readOnly = false;
        this._diffMarked = null;
        this._markedProblems = null;
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
