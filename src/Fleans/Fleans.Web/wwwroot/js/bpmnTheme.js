// Diagram themes shared by the editor (bpmnEditor.js) and the viewer (bpmnViewer.js).
// 'orbit' (default) is the Admin UI look (css/orbit.css); 'classic' is plain bpmn-js recoloured by
// app.css; 'archify' is a gridded monospace style. Picked in Settings → Diagram Style and persisted
// in localStorage. The theme is read when a modeler/viewer is
// created, so a change applies to the next diagram opened (Settings is a separate page anyway).
//
// Archify: bpmn-js writes fill/stroke/label colours as inline styles, so the renderer defaults below are
// CSS variables: every shape resolves them from its own <g> element. The category module tags
// each shape with an `fl-cat-*` class, and app.css maps each class to a colour pair — so a shape
// is recoloured purely by CSS (light/dark follow the Fluent theme) and per-element DI colours
// (bioc:/color: attributes) still win because they replace the default instead of the variable.
(function () {
    var CATEGORY_PREFIX = 'fl-cat-';
    var STORAGE_KEY = 'fleans.diagramTheme';
    var THEMES = ['orbit', 'classic', 'archify'];
    var DEFAULT_THEME = 'orbit';
    var SVG_NS = 'http://www.w3.org/2000/svg';

    function eventCategory(bo) {
        var defs = bo.eventDefinitions || [];
        if (defs.length === 0) return 'neutral';
        switch (defs[0].$type) {
            case 'bpmn:TimerEventDefinition':
                return 'timer';
            case 'bpmn:MessageEventDefinition':
            case 'bpmn:SignalEventDefinition':
                return 'message';
            case 'bpmn:ErrorEventDefinition':
            case 'bpmn:EscalationEventDefinition':
            case 'bpmn:CompensateEventDefinition':
            case 'bpmn:CancelEventDefinition':
            case 'bpmn:TerminateEventDefinition':
                return 'error';
            default:
                return 'neutral';
        }
    }

    function categoryOf(element) {
        var bo = element.businessObject;
        if (!bo || element.labelTarget) return null;
        switch (bo.$type) {
            case 'bpmn:UserTask':
            case 'bpmn:ManualTask':
                return 'human';
            case 'bpmn:Task':
            case 'bpmn:ScriptTask':
            case 'bpmn:ServiceTask':
            case 'bpmn:BusinessRuleTask':
                return 'compute';
            case 'bpmn:SendTask':
            case 'bpmn:ReceiveTask':
                return 'message';
            case 'bpmn:SubProcess':
            case 'bpmn:AdHocSubProcess':
            case 'bpmn:Transaction':
            case 'bpmn:CallActivity':
                return 'process';
            case 'bpmn:StartEvent':
            case 'bpmn:EndEvent':
            case 'bpmn:IntermediateCatchEvent':
            case 'bpmn:IntermediateThrowEvent':
            case 'bpmn:BoundaryEvent':
                return eventCategory(bo);
            default:
                return 'neutral';
        }
    }

    function CategoryMarkers(eventBus, elementRegistry) {
        function apply(element) {
            var gfx = elementRegistry.getGraphics(element);
            if (!gfx || element.waypoints) return;
            var category = categoryOf(element);
            Array.from(gfx.classList)
                .filter(function (c) { return c.indexOf(CATEGORY_PREFIX) === 0; })
                .forEach(function (c) { gfx.classList.remove(c); });
            if (category) gfx.classList.add(CATEGORY_PREFIX + category);
        }

        // shape.added covers import, palette create and replace (a replaced shape is a new shape);
        // shape.changed covers in-place edits such as swapping an event definition.
        eventBus.on(['shape.added', 'shape.changed'], function (e) { apply(e.element); });
    }

    CategoryMarkers.$inject = ['eventBus', 'elementRegistry'];

    var FONT_FAMILY = "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";

    var ARCHIFY = {
        module: {
            __init__: ['fleansCategoryMarkers'],
            fleansCategoryMarkers: ['type', CategoryMarkers]
        },
        config: {
            bpmnRenderer: {
                defaultFillColor: 'var(--fl-node-fill)',
                defaultStrokeColor: 'var(--fl-node-stroke)',
                defaultLabelColor: 'var(--fl-text)'
            },
            textRenderer: {
                defaultStyle: { fontFamily: FONT_FAMILY, fontSize: 11 },
                externalStyle: { fontFamily: FONT_FAMILY, fontSize: 10 }
            }
        }
    };

    // ---- orbit -----------------------------------------------------------------------------
    // Shape kind drives the Orbit colour (css/orbit.css): o-kind-start green, -end red, -gateway amber,
    // -event by its definition (fl-cat-* from CategoryMarkers: timer/message violet, error red),
    // -task a neutral 12px card with a mono kind label, -container/-pool/-annotation/-data muted.
    var KIND_PREFIX = 'o-kind-';

    function kindOf(element) {
        var bo = element.businessObject;
        if (!bo || element.labelTarget || element.waypoints) return null;
        var type = bo.$type;
        if (type === 'bpmn:StartEvent') return 'start';
        if (type === 'bpmn:EndEvent') return 'end';
        if (/Event$/.test(type)) return 'event';
        if (/Gateway$/.test(type)) return 'gateway';
        if (type === 'bpmn:SubProcess' || type === 'bpmn:AdHocSubProcess' || type === 'bpmn:Transaction') {
            return element.collapsed ? 'task' : 'container';
        }
        if (type === 'bpmn:CallActivity' || /Task$/.test(type)) return 'task';
        if (type === 'bpmn:Participant' || type === 'bpmn:Lane') return 'pool';
        if (type === 'bpmn:TextAnnotation' || type === 'bpmn:Group') return 'annotation';
        if (/^bpmn:Data/.test(type)) return 'data';
        return null;
    }

    // Mono caption drawn in a task's top-left corner (design: "SERVICE", "USER", ...).
    var TASK_CAPTIONS = {
        'bpmn:UserTask': 'USER',
        'bpmn:ServiceTask': 'SERVICE',
        'bpmn:ScriptTask': 'SCRIPT',
        'bpmn:BusinessRuleTask': 'RULE',
        'bpmn:SendTask': 'SEND',
        'bpmn:ReceiveTask': 'RECEIVE',
        'bpmn:ManualTask': 'MANUAL',
        'bpmn:CallActivity': 'CALL',
        'bpmn:SubProcess': 'SUB-PROCESS',
        'bpmn:AdHocSubProcess': 'AD-HOC',
        'bpmn:Transaction': 'TRANSACTION'
    };

    // Types whose bpmn-js marker icon sits in the top-left corner; the caption goes right of it.
    var ICON_TYPES = ['bpmn:UserTask', 'bpmn:ServiceTask', 'bpmn:ScriptTask', 'bpmn:BusinessRuleTask',
        'bpmn:SendTask', 'bpmn:ReceiveTask', 'bpmn:ManualTask'];

    function captionOf(element) {
        if (kindOf(element) !== 'task' || element.width < 70) return null;
        var bo = element.businessObject;
        var caption = TASK_CAPTIONS[bo.$type];
        if (!caption) return null;
        var multi = bo.loopCharacteristics;
        if (multi && multi.$type === 'bpmn:MultiInstanceLoopCharacteristics') {
            caption += multi.isSequential ? ' ≡' : ' ⦀';
        }
        return caption;
    }

    function OrbitKinds(eventBus, elementRegistry) {
        function apply(element) {
            var gfx = elementRegistry.getGraphics(element);
            if (!gfx || element.waypoints) return;
            Array.from(gfx.classList)
                .filter(function (c) { return c.indexOf(KIND_PREFIX) === 0; })
                .forEach(function (c) { gfx.classList.remove(c); });
            var kind = kindOf(element);
            if (kind) gfx.classList.add(KIND_PREFIX + kind);
        }

        eventBus.on(['shape.added', 'shape.changed'], function (e) { apply(e.element); });
    }

    OrbitKinds.$inject = ['eventBus', 'elementRegistry'];

    // Draws on top of the stock bpmn-js renderer instead of replacing it, so every BPMN element keeps
    // its standard notation (markers, event definitions, loop/compensation icons). BaseRenderer is not
    // exported by the UMD bundle, so this registers the same 'render.shape' hook it would, at a higher
    // priority; returning the visual stops the stock listener from drawing a second copy.
    function OrbitRenderer(eventBus, bpmnRenderer) {
        eventBus.on('render.shape', 1500, function (event, context) {
            var element = context.element;
            var caption = captionOf(element);
            if (!caption) return;

            var visual = bpmnRenderer.drawShape(context.gfx, element, context.attrs);
            var text = document.createElementNS(SVG_NS, 'text');
            var hasIcon = ICON_TYPES.indexOf(element.businessObject.$type) >= 0;
            text.setAttribute('x', hasIcon ? 30 : 10);
            text.setAttribute('y', 16);
            text.setAttribute('class', 'o-kind-label');
            text.textContent = caption;
            context.gfx.appendChild(text);
            return visual;
        });
    }

    OrbitRenderer.$inject = ['eventBus', 'bpmnRenderer'];

    var BODY_FONT = "'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif";

    var ORBIT = {
        module: {
            __init__: ['fleansCategoryMarkers', 'orbitKinds', 'orbitRenderer'],
            fleansCategoryMarkers: ['type', CategoryMarkers],
            orbitKinds: ['type', OrbitKinds],
            orbitRenderer: ['type', OrbitRenderer]
        },
        config: {
            bpmnRenderer: {
                defaultFillColor: 'var(--o-node-fill)',
                defaultStrokeColor: 'var(--o-node-stroke)',
                defaultLabelColor: 'var(--o-text)'
            },
            textRenderer: {
                defaultStyle: { fontFamily: BODY_FONT, fontSize: 12, fontWeight: 500 },
                externalStyle: { fontFamily: BODY_FONT, fontSize: 11 }
            }
        }
    };

    var THEME_DEFS = { orbit: ORBIT, archify: ARCHIFY };

    // One level deeper than Object.assign: caller options win per key, so a bpmnRenderer /
    // textRenderer option passed by bpmnEditor.js or bpmnViewer.js survives the theme config.
    function mergeConfig(themeConfig, options) {
        var merged = Object.assign({}, themeConfig, options);
        Object.keys(themeConfig).forEach(function (key) {
            if (!options[key]) return;
            merged[key] = Object.assign({}, themeConfig[key], options[key]);
            Object.keys(themeConfig[key]).forEach(function (sub) {
                var themeValue = themeConfig[key][sub], callerValue = options[key][sub];
                if (callerValue && typeof themeValue === 'object' && typeof callerValue === 'object') {
                    merged[key][sub] = Object.assign({}, themeValue, callerValue);
                }
            });
        });
        return merged;
    }

    window.fleansBpmnTheme = {
        // Storage can be blocked (strict private mode, sandboxed iframe) — fall back to the default
        // rather than failing the editor/viewer build.
        get: function () {
            try {
                var value = localStorage.getItem(STORAGE_KEY);
                return THEMES.indexOf(value) >= 0 ? value : DEFAULT_THEME;
            } catch (e) {
                return DEFAULT_THEME;
            }
        },

        // Returns false when the choice could not be persisted.
        set: function (value) {
            if (THEMES.indexOf(value) < 0) return false;
            try {
                localStorage.setItem(STORAGE_KEY, value);
                return true;
            } catch (e) {
                return false;
            }
        },

        // Builds a bpmn-js Modeler/Viewer with the stored theme applied. The container always gets
        // `fl-diagram-<theme>`; app.css scopes each theme's rules under that class.
        create: function (BpmnCtor, options) {
            var theme = this.get();
            var def = THEME_DEFS[theme];
            var instance = def
                ? new BpmnCtor(mergeConfig(def.config, Object.assign({}, options, {
                    additionalModules: [def.module].concat(options.additionalModules || [])
                })))
                : new BpmnCtor(options);
            instance.get('canvas').getContainer().classList.add('fl-diagram-' + theme);
            return instance;
        }
    };
})();
