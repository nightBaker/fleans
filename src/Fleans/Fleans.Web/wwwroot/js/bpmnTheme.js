// Diagram themes shared by the editor (bpmnEditor.js) and the viewer (bpmnViewer.js).
// 'classic' (default) is plain bpmn-js recoloured by app.css; 'archify' is opt-in from
// Settings → Diagram Style and persisted in localStorage. The theme is read when a modeler/viewer is
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
    var THEMES = ['classic', 'archify'];

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
        // Storage can be blocked (strict private mode, sandboxed iframe) — fall back to classic
        // rather than failing the editor/viewer build.
        get: function () {
            try {
                var value = localStorage.getItem(STORAGE_KEY);
                return THEMES.indexOf(value) >= 0 ? value : 'classic';
            } catch (e) {
                return 'classic';
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
            var instance = theme === 'archify'
                ? new BpmnCtor(mergeConfig(ARCHIFY.config, Object.assign({}, options, {
                    additionalModules: [ARCHIFY.module].concat(options.additionalModules || [])
                })))
                : new BpmnCtor(options);
            instance.get('canvas').getContainer().classList.add('fl-diagram-' + theme);
            return instance;
        }
    };
})();
