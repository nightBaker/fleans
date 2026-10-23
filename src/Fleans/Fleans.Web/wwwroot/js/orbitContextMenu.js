// Orbit right-click menus for the BPMN editor (design board "Context menus"): one menu for an element,
// one for the empty canvas and one for a connection, each with keyboard shortcuts, one submenu level
// and edge flipping. "Change type" / "Attach boundary event" hand over to the stock bpmn-js popup menus
// ('bpmn-replace' / 'boundary-event'), so every BPMN element bpmn-js can replace is reachable.
//
// Also exports window.orbitElementCatalog — the element list shared by the editor's left-rail palette
// (Editor.razor renders tiles with data-bpmn-create="<key>", bpmnEditor.bindChrome wires them) and the
// canvas menu's "Add element" submenu.
(function () {
    var CATALOG = [
        { key: 'start', label: 'Start event', type: 'bpmn:StartEvent' },
        { key: 'end', label: 'End event', type: 'bpmn:EndEvent' },
        { key: 'user-task', label: 'User task', type: 'bpmn:UserTask' },
        { key: 'service-task', label: 'Service task', type: 'bpmn:ServiceTask' },
        { key: 'script-task', label: 'Script task', type: 'bpmn:ScriptTask' },
        { key: 'call-activity', label: 'Call activity', type: 'bpmn:CallActivity' },
        { key: 'exclusive-gateway', label: 'Exclusive gateway', type: 'bpmn:ExclusiveGateway' },
        { key: 'parallel-gateway', label: 'Parallel gateway', type: 'bpmn:ParallelGateway' },
        { key: 'event-gateway', label: 'Event-based gateway', type: 'bpmn:EventBasedGateway' },
        { key: 'timer', label: 'Timer event', type: 'bpmn:IntermediateCatchEvent', eventDefinitionType: 'bpmn:TimerEventDefinition' },
        { key: 'message', label: 'Message event', type: 'bpmn:IntermediateCatchEvent', eventDefinitionType: 'bpmn:MessageEventDefinition' },
        { key: 'signal', label: 'Signal event', type: 'bpmn:IntermediateCatchEvent', eventDefinitionType: 'bpmn:SignalEventDefinition' },
        { key: 'subprocess', label: 'Sub-process', type: 'bpmn:SubProcess', isExpanded: true },
        { key: 'annotation', label: 'Comment', type: 'bpmn:TextAnnotation' },
        // Drawn only: the engine deploys the first pool's process and ignores data (the lint says so).
        { key: 'pool', label: 'Pool', type: 'bpmn:Participant', participant: true },
        { key: 'data-object', label: 'Data object', type: 'bpmn:DataObjectReference' },
        { key: 'data-store', label: 'Data store', type: 'bpmn:DataStoreReference' }
    ];

    function findEntry(key) {
        for (var i = 0; i < CATALOG.length; i++) {
            if (CATALOG[i].key === key) return CATALOG[i];
        }
        return null;
    }

    window.orbitElementCatalog = {
        entries: CATALOG,
        // Returns a new (not yet placed) diagram shape for a catalog key, or null for an unknown key.
        createShape: function (elementFactory, key) {
            var entry = findEntry(key);
            if (!entry) return null;
            if (entry.participant) return elementFactory.createParticipantShape();
            var attrs = { type: entry.type };
            if (entry.eventDefinitionType) attrs.eventDefinitionType = entry.eventDefinitionType;
            if (entry.isExpanded) attrs.isExpanded = true;
            return elementFactory.createShape(attrs);
        }
    };

    var BOUNDARABLE = ['bpmn:Task', 'bpmn:UserTask', 'bpmn:ServiceTask', 'bpmn:ScriptTask',
        'bpmn:SendTask', 'bpmn:ReceiveTask', 'bpmn:ManualTask', 'bpmn:BusinessRuleTask',
        'bpmn:CallActivity', 'bpmn:SubProcess', 'bpmn:Transaction'];

    var CHEVRON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" aria-hidden="true"><path d="m9 6 6 6-6 6"/></svg>';

    function typeCaption(bo) {
        return (bo.$type || '').replace(/^bpmn:/, '').replace(/([a-z])([A-Z])/g, '$1 $2').toUpperCase();
    }

    function isMac() {
        return /Mac|iPhone|iPad/.test(navigator.platform || '');
    }

    function keyLabel(key) {
        return isMac() ? key.replace(/^Ctrl /, '⌘ ') : key;
    }

    function OrbitContextMenu(eventBus, canvas, injector) {
        this._eventBus = eventBus;
        this._canvas = canvas;
        this._injector = injector;
        this._menu = null;
        this._sub = null;
        this._enabled = true;

        var self = this;

        eventBus.on('element.contextmenu', function (event) {
            var original = event.originalEvent;
            if (!original) return;
            original.preventDefault();
            original.stopPropagation();
            self.open(self._resolveTarget(event.element, original), { x: original.clientX, y: original.clientY });
        });

        // The menu belongs to one viewbox and one diagram: anything that moves or replaces it closes the menu.
        eventBus.on(['canvas.viewbox.changing', 'import.parse.start', 'diagram.destroy', 'popupMenu.open'], function () {
            self.close();
        });

        this._onOutside = function (e) {
            if (self._menu && !self._menu.contains(e.target) && !(self._sub && self._sub.contains(e.target))) {
                self.close();
            }
        };

        this._registerShortcuts();
    }

    OrbitContextMenu.$inject = ['eventBus', 'canvas', 'injector'];

    // A hovered connection is covered by its bendpoint / segment-dragger handles, which live outside
    // the element's graphics, so diagram-js reports the root. The handles carry the connection's
    // data-element-id — use it.
    OrbitContextMenu.prototype._resolveTarget = function (element, original) {
        if (element !== this._canvas.getRootElement() || !original.target || !original.target.closest) return element;
        var tagged = original.target.closest('[data-element-id]');
        var resolved = tagged && this._get('elementRegistry').get(tagged.getAttribute('data-element-id'));
        return resolved || element;
    };

    OrbitContextMenu.prototype._get = function (name) {
        return this._injector.get(name, false);
    };

    // ---- Actions ------------------------------------------------------------------------------

    OrbitContextMenu.prototype._targets = function (element) {
        var selection = this._get('selection');
        var selected = selection ? selection.get() : [];
        return selected.indexOf(element) >= 0 ? selected.slice() : [element];
    };

    OrbitContextMenu.prototype._toDiagramPoint = function (clientPoint) {
        var rect = this._canvas.getContainer().getBoundingClientRect();
        var viewbox = this._canvas.viewbox();
        return {
            x: Math.round(viewbox.x + (clientPoint.x - rect.left) / viewbox.scale),
            y: Math.round(viewbox.y + (clientPoint.y - rect.top) / viewbox.scale)
        };
    };

    OrbitContextMenu.prototype.copy = function (elements) {
        this._get('copyPaste').copy(elements);
    };

    OrbitContextMenu.prototype.cut = function (elements) {
        this._get('copyPaste').copy(elements);
        this._get('modeling').removeElements(elements.slice());
    };

    OrbitContextMenu.prototype.remove = function (elements) {
        this._get('modeling').removeElements(elements.slice());
    };

    // Duplicate = copy + paste 40px down-right, keeping whatever the user had on the clipboard.
    OrbitContextMenu.prototype.duplicate = function (elements) {
        var copyPaste = this._get('copyPaste');
        var clipboard = this._get('clipboard');
        var shapes = elements.filter(function (e) { return !e.waypoints && !e.labelTarget; });
        if (!shapes.length) return;
        var previous = clipboard.get();
        copyPaste.copy(shapes);
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        shapes.forEach(function (s) {
            minX = Math.min(minX, s.x); minY = Math.min(minY, s.y);
            maxX = Math.max(maxX, s.x + s.width); maxY = Math.max(maxY, s.y + s.height);
        });
        copyPaste.paste({
            element: shapes[0].parent,
            point: { x: (minX + maxX) / 2 + 40, y: (minY + maxY) / 2 + 40 }
        });
        clipboard.set(previous);
    };

    OrbitContextMenu.prototype.pasteAt = function (clientPoint) {
        this._get('copyPaste').paste({
            element: this._canvas.getRootElement(),
            point: this._toDiagramPoint(clientPoint)
        });
    };

    OrbitContextMenu.prototype.addAt = function (key, clientPoint) {
        var shape = window.orbitElementCatalog.createShape(this._get('elementFactory'), key);
        if (!shape) return;
        var created = this._get('modeling').createShape(shape, this._toDiagramPoint(clientPoint), this._canvas.getRootElement());
        this._get('selection').select(created);
    };

    OrbitContextMenu.prototype.comment = function (element) {
        var shape = this._get('elementFactory').createShape({ type: 'bpmn:TextAnnotation' });
        this._get('autoPlace').append(element, shape);
    };

    OrbitContextMenu.prototype.editLabel = function (element) {
        this._get('selection').select(element);
        this._get('directEditing').activate(element);
    };

    OrbitContextMenu.prototype.openProperties = function (element, field) {
        this._get('selection').select(element);
        this._eventBus.fire('orbit.openProperties', { element: element, field: field || null });
    };

    OrbitContextMenu.prototype.openPopup = function (element, providerId, clientPoint, title) {
        this._get('popupMenu').open(element, providerId, {
            x: clientPoint.x,
            y: clientPoint.y,
            cursor: { x: clientPoint.x, y: clientPoint.y }
        }, { title: title, width: 300 });
    };

    OrbitContextMenu.prototype.fit = function () {
        this._canvas.zoom('fit-viewport', 'auto');
    };

    OrbitContextMenu.prototype.download = async function (format) {
        var modeler = window.bpmnEditor && window.bpmnEditor._modeler;
        if (!modeler) return;
        var process = this._canvas.getRootElement().businessObject;
        var name = (process && process.id) || 'diagram';
        var result = format === 'svg' ? await modeler.saveSVG() : await modeler.saveXML({ format: true });
        var blob = new Blob([format === 'svg' ? result.svg : result.xml],
            { type: format === 'svg' ? 'image/svg+xml' : 'application/xml' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = name + (format === 'svg' ? '.svg' : '.bpmn');
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 0);
    };

    OrbitContextMenu.prototype._supportsDefault = function (flow) {
        var source = flow.source && flow.source.businessObject;
        return !!(source && source.$descriptor && source.$descriptor.propertiesByName['default'] &&
            flow.businessObject.$type === 'bpmn:SequenceFlow');
    };

    OrbitContextMenu.prototype.toggleDefault = function (flow) {
        var source = flow.source;
        var isDefault = source.businessObject['default'] === flow.businessObject;
        this._get('modeling').updateProperties(source, { 'default': isDefault ? undefined : flow.businessObject });
        this._eventBus.fire('orbit.elementChanged', { element: flow });
    };

    OrbitContextMenu.prototype._canReverse = function (flow) {
        var rules = this._get('rules');
        return !!rules.allowed('connection.reconnect', {
            connection: flow, source: flow.target, target: flow.source
        });
    };

    OrbitContextMenu.prototype.reverse = function (flow) {
        var modeling = this._get('modeling');
        var points = flow.waypoints.slice().reverse().map(function (p) { return { x: p.x, y: p.y }; });
        modeling.reconnect(flow, flow.target, flow.source, points);
        modeling.layoutConnection(flow);
    };

    // ---- Menus --------------------------------------------------------------------------------

    function item(label, action, opts) {
        var o = opts || {};
        return { label: label, action: action, key: o.key, danger: !!o.danger, disabled: !!o.disabled, sub: o.sub, title: o.title };
    }

    var SEP = { separator: true };

    OrbitContextMenu.prototype._elementMenu = function (element, at) {
        var self = this;
        var bo = element.businessObject;
        var targets = this._targets(element);
        var shapes = targets.filter(function (e) { return !e.waypoints; });
        var single = targets.length === 1;
        var replaceable = single && !this._get('popupMenu').isEmpty(element, 'bpmn-replace');
        var items = [
            item('Cut', function () { self.cut(targets); }, { key: 'Ctrl X' }),
            item('Copy', function () { self.copy(targets); }, { key: 'Ctrl C' }),
            item('Duplicate', function () { self.duplicate(shapes); }, { key: 'Ctrl D', disabled: !shapes.length }),
            SEP
        ];
        if (replaceable) {
            items.push(item('Change type', function (point) { self.openPopup(element, 'bpmn-replace', point || at, 'Change type'); }, { sub: 'popup' }));
        }
        if (single && BOUNDARABLE.indexOf(bo.$type) >= 0) {
            items.push(item('Attach boundary event', function (point) { self.openPopup(element, 'boundary-event', point || at, 'Boundary event'); }, { sub: 'popup' }));
        }
        if (shapes.length > 1) {
            items.push(item('Align', null, {
                sub: function () {
                    var align = self._get('alignElements');
                    var distribute = self._get('distributeElements');
                    var a = function (type) { return function () { align.trigger(shapes, type); }; };
                    return [
                        item('Left', a('left')), item('Center', a('center')), item('Right', a('right')), SEP,
                        item('Top', a('top')), item('Middle', a('middle')), item('Bottom', a('bottom')), SEP,
                        item('Distribute horizontally', function () { distribute.trigger(shapes, 'horizontal'); }, { disabled: shapes.length < 3 }),
                        item('Distribute vertically', function () { distribute.trigger(shapes, 'vertical'); }, { disabled: shapes.length < 3 })
                    ];
                }
            }));
        }
        items.push(SEP);
        if (single) {
            if (bo.$type !== 'bpmn:TextAnnotation') {
                items.push(item('Add comment', function () { self.comment(element); }));
            }
            items.push(item('Edit label', function () { self.editLabel(element); }, { key: 'F2' }));
            items.push(item('Open properties', function () { self.openProperties(element); }, { key: 'Ctrl I' }));
            if (bo.$instanceOf && bo.$instanceOf('bpmn:FlowNode')) {
                items.push(item('Simulate from here', function () { self._eventBus.fire('orbit.simulateFrom', { element: element }); }));
            }
            items.push(SEP);
        }
        items.push(item('Delete', function () { self.remove(targets); }, { key: 'Del', danger: true }));
        return {
            title: single ? typeCaption(bo) + ' · ' + bo.id : targets.length + ' elements selected',
            items: items
        };
    };

    OrbitContextMenu.prototype._canvasMenu = function (at) {
        var self = this;
        var clipboardEmpty = this._get('clipboard').isEmpty();
        var editorActions = this._get('editorActions');
        return {
            title: 'Canvas',
            items: [
                item('Paste', function () { editorActions.trigger('paste'); }, { key: 'Ctrl V', disabled: clipboardEmpty }),
                item('Paste here', function () { self.pasteAt(at); }, { disabled: clipboardEmpty }),
                SEP,
                item('Add element', null, {
                    sub: function () {
                        return CATALOG.map(function (entry) {
                            return item(entry.label, function () { self.addAt(entry.key, at); });
                        });
                    }
                }),
                item('Select all', function () { editorActions.trigger('selectElements'); }, { key: 'Ctrl A' }),
                SEP,
                item('Fit to screen', function () { self.fit(); }, { key: 'Shift 1' }),
                item('Zoom to 100%', function () { self._canvas.zoom(1, 'auto'); }),
                SEP,
                item('Export', null, {
                    sub: function () {
                        return [
                            item('Download .bpmn', function () { self.download('bpmn'); }),
                            item('Download .svg', function () { self.download('svg'); })
                        ];
                    }
                }),
                item('Show XML', function () { self._eventBus.fire('orbit.showXml'); }, { key: 'Ctrl E' })
            ]
        };
    };

    OrbitContextMenu.prototype._connectionMenu = function (flow) {
        var self = this;
        var bo = flow.businessObject;
        var isSequence = bo.$type === 'bpmn:SequenceFlow';
        var items = [];
        if (isSequence) {
            var supportsDefault = this._supportsDefault(flow);
            var isDefault = supportsDefault && flow.source.businessObject['default'] === bo;
            items.push(item('Set condition…', function () { self.openProperties(flow, 'condition'); }));
            items.push(item(isDefault ? 'Unset default flow' : 'Make default flow', function () { self.toggleDefault(flow); }, {
                disabled: !supportsDefault,
                title: supportsDefault ? null : 'Only flows leaving an exclusive, inclusive or complex gateway or an activity can be the default'
            }));
            items.push(SEP);
        }
        items.push(item('Re-route', function () { self._get('modeling').layoutConnection(flow); }));
        if (bo.$type !== 'bpmn:Association') {
            items.push(item('Edit label', function () { self.editLabel(flow); }, { key: 'F2' }));
        }
        items.push(SEP);
        if (isSequence) {
            var canReverse = this._canReverse(flow);
            items.push(item('Reverse direction', function () { self.reverse(flow); }, {
                disabled: !canReverse,
                title: canReverse ? null : 'BPMN rules do not allow this connection in the other direction'
            }));
        }
        items.push(item('Delete', function () { self.remove(self._targets(flow)); }, { key: 'Del', danger: true }));
        return { title: typeCaption(bo), items: items };
    };

    OrbitContextMenu.prototype._menuFor = function (element, at) {
        if (element.labelTarget) element = element.labelTarget;
        if (element === this._canvas.getRootElement()) return this._canvasMenu(at);
        var selection = this._get('selection');
        if (selection && selection.get().indexOf(element) < 0) selection.select(element);
        return element.waypoints ? this._connectionMenu(element) : this._elementMenu(element, at);
    };

    // ---- DOM ----------------------------------------------------------------------------------

    // Off in the editor's read-only modes (Simulation, History).
    OrbitContextMenu.prototype.setEnabled = function (enabled) {
        this._enabled = !!enabled;
        if (!this._enabled) this.close();
    };

    OrbitContextMenu.prototype.open = function (element, clientPoint) {
        this.close();
        if (!this._enabled) return;
        var spec = this._menuFor(element, clientPoint);
        this._menu = this._render(spec, false);
        document.body.appendChild(this._menu);
        this._place(this._menu, clientPoint.x, clientPoint.y, null);
        document.addEventListener('pointerdown', this._onOutside, true);
        window.addEventListener('blur', this._closeBound = this.close.bind(this));
        window.addEventListener('resize', this._closeBound);
        this._focusFirst(this._menu);
    };

    // Shift+F10 / the ContextMenu key: open for the selection (at its centre) or for the canvas.
    OrbitContextMenu.prototype.openForSelection = function () {
        var selection = this._get('selection').get();
        var container = this._canvas.getContainer().getBoundingClientRect();
        var viewbox = this._canvas.viewbox();
        var target = selection.length ? selection[0] : this._canvas.getRootElement();
        var point = { x: container.left + 40, y: container.top + 40 };
        if (selection.length && !target.waypoints) {
            point = {
                x: container.left + (target.x + target.width - viewbox.x) * viewbox.scale,
                y: container.top + (target.y + target.height / 2 - viewbox.y) * viewbox.scale
            };
        }
        this.open(target, point);
    };

    OrbitContextMenu.prototype.close = function () {
        this._closeSub();
        if (this._menu) {
            this._menu.remove();
            this._menu = null;
            document.removeEventListener('pointerdown', this._onOutside, true);
            window.removeEventListener('blur', this._closeBound);
            window.removeEventListener('resize', this._closeBound);
            // Back to the element keyboard shortcuts are bound to (the editor's focusable canvas).
            var keyboard = this._get('keyboard');
            var binding = keyboard && keyboard.getBinding();
            if (binding && binding.focus) binding.focus({ preventScroll: true });
        }
    };

    OrbitContextMenu.prototype._closeSub = function () {
        if (this._sub) {
            this._sub.remove();
            this._sub = null;
            if (this._subOwner) this._subOwner.setAttribute('aria-expanded', 'false');
            this._subOwner = null;
        }
    };

    OrbitContextMenu.prototype._render = function (spec, isSub) {
        var self = this;
        var menu = document.createElement('div');
        menu.className = 'orbit-menu' + (isSub ? ' orbit-menu--sub' : '');
        menu.setAttribute('role', 'menu');
        if (spec.title) {
            menu.setAttribute('aria-label', spec.title);
            var title = document.createElement('div');
            title.className = 'orbit-menu__title';
            title.textContent = spec.title;
            menu.appendChild(title);
        }
        spec.items.forEach(function (entry) {
            if (entry.separator) {
                var sep = document.createElement('div');
                sep.className = 'orbit-menu__sep';
                sep.setAttribute('role', 'separator');
                menu.appendChild(sep);
                return;
            }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'orbit-menu__item' + (entry.danger ? ' orbit-menu__item--danger' : '');
            btn.setAttribute('role', 'menuitem');
            btn.tabIndex = -1;
            if (entry.title) btn.title = entry.title;
            if (entry.disabled) btn.setAttribute('aria-disabled', 'true');
            var label = document.createElement('span');
            label.textContent = entry.label;
            btn.appendChild(label);
            if (entry.key) {
                var key = document.createElement('span');
                key.className = 'orbit-menu__key';
                key.textContent = keyLabel(entry.key);
                btn.appendChild(key);
            } else if (entry.sub) {
                btn.setAttribute('aria-haspopup', entry.sub === 'popup' ? 'dialog' : 'menu');
                btn.insertAdjacentHTML('beforeend', CHEVRON);
            }
            if (typeof entry.sub === 'function') btn.setAttribute('aria-expanded', 'false');

            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                self._activate(entry, btn);
            });
            btn.addEventListener('mouseenter', function () {
                if (!isSub) {
                    if (typeof entry.sub === 'function' && !entry.disabled) self._openSub(entry, btn);
                    else self._closeSub();
                }
                btn.focus({ preventScroll: true });
            });
            menu.appendChild(btn);
        });
        menu.addEventListener('keydown', function (e) { self._onKey(e, menu, isSub); });
        menu.addEventListener('contextmenu', function (e) { e.preventDefault(); });
        return menu;
    };

    OrbitContextMenu.prototype._activate = function (entry, btn) {
        if (entry.disabled) return;
        if (typeof entry.sub === 'function') {
            this._openSub(entry, btn);
            this._focusFirst(this._sub);
            return;
        }
        var rect = btn.getBoundingClientRect();
        var point = { x: rect.right + 4, y: rect.top };
        this.close();
        if (entry.action) entry.action(point);
    };

    OrbitContextMenu.prototype._openSub = function (entry, btn) {
        if (this._subOwner === btn) return;
        this._closeSub();
        this._sub = this._render({ title: null, items: entry.sub() }, true);
        this._sub.setAttribute('aria-label', entry.label);
        document.body.appendChild(this._sub);
        var rect = btn.getBoundingClientRect();
        this._place(this._sub, rect.right + 4, rect.top - 6, rect.left - 4);
        this._subOwner = btn;
        btn.setAttribute('aria-expanded', 'true');
    };

    // Keeps the menu inside the window: at the right edge it opens to the left (for a submenu, to the
    // left of its parent item, `flipX`), at the bottom it moves up.
    OrbitContextMenu.prototype._place = function (menu, x, y, flipX) {
        var margin = 8;
        var w = menu.offsetWidth, h = menu.offsetHeight;
        var left = x, top = y;
        if (left + w > window.innerWidth - margin) {
            left = (flipX != null ? flipX : x) - w;
        }
        if (top + h > window.innerHeight - margin) {
            top = window.innerHeight - margin - h;
        }
        menu.style.left = Math.max(margin, left) + 'px';
        menu.style.top = Math.max(margin, top) + 'px';
    };

    OrbitContextMenu.prototype._focusFirst = function (menu) {
        var first = menu && menu.querySelector('.orbit-menu__item:not([aria-disabled="true"])');
        if (first) first.focus({ preventScroll: true });
    };

    OrbitContextMenu.prototype._onKey = function (e, menu, isSub) {
        var items = Array.from(menu.querySelectorAll('.orbit-menu__item'));
        var enabled = items.filter(function (b) { return b.getAttribute('aria-disabled') !== 'true'; });
        var index = enabled.indexOf(document.activeElement);
        switch (e.key) {
            case 'ArrowDown':
                enabled[(index + 1) % enabled.length].focus();
                break;
            case 'ArrowUp':
                enabled[(index - 1 + enabled.length) % enabled.length].focus();
                break;
            case 'Home':
                enabled[0].focus();
                break;
            case 'End':
                enabled[enabled.length - 1].focus();
                break;
            case 'ArrowRight':
                if (!isSub && document.activeElement.getAttribute('aria-expanded') === 'false') {
                    document.activeElement.click();
                }
                break;
            case 'ArrowLeft':
                if (isSub && this._subOwner) {
                    var owner = this._subOwner;
                    this._closeSub();
                    owner.focus();
                }
                break;
            case 'Escape':
                if (isSub && this._subOwner) {
                    var parent = this._subOwner;
                    this._closeSub();
                    parent.focus();
                } else {
                    this.close();
                }
                break;
            case 'Tab':
                this.close();
                break;
            default:
                return;
        }
        e.preventDefault();
        e.stopPropagation();
    };

    // ---- Shortcuts the stock keyboard bindings lack (design board "Context menus") -------------

    OrbitContextMenu.prototype._registerShortcuts = function () {
        var self = this;
        var keyboard = this._get('keyboard');
        if (!keyboard) return;

        // Above the stock bindings (priority 1000); returning true also stops the browser default
        // (Ctrl+D would otherwise bookmark the page). C stays bpmn-js' global-connect key.
        keyboard.addListener(1500, function (context) {
            var e = context.keyEvent;
            var selection = self._get('selection').get();
            var single = selection.length === 1 ? selection[0] : null;

            if ((e.key === 'F10' && keyboard.isShift(e)) || e.key === 'ContextMenu') {
                self.openForSelection();
                return true;
            }
            if (keyboard.isCmd(e) && keyboard.isKey(['x', 'X'], e) && selection.length) {
                self.cut(selection);
                return true;
            }
            if (keyboard.isCmd(e) && keyboard.isKey(['d', 'D'], e) && selection.length) {
                self.duplicate(selection);
                return true;
            }
            if (keyboard.isCmd(e) && keyboard.isKey(['i', 'I'], e) && single) {
                self.openProperties(single);
                return true;
            }
            if (keyboard.isCmd(e) && keyboard.isKey(['e', 'E'], e)) {
                self._eventBus.fire('orbit.showXml');
                return true;
            }
            if (keyboard.isShift(e) && !keyboard.isCmd(e) && (e.code === 'Digit1' || e.key === '!')) {
                self.fit();
                return true;
            }
            if (e.key === 'F2' && single) {
                self.editLabel(single);
                return true;
            }
        });
    };

    window.orbitContextMenuModule = {
        __init__: ['orbitContextMenu'],
        orbitContextMenu: ['type', OrbitContextMenu]
    };
})();
