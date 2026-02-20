(function () {
    var BOUNDARABLE_TYPES = [
        'bpmn:Task',
        'bpmn:UserTask',
        'bpmn:ServiceTask',
        'bpmn:ScriptTask',
        'bpmn:CallActivity',
        'bpmn:SubProcess'
    ];

    function BoundaryEventContextPadProvider(contextPad, modeling, elementFactory, bpmnFactory, selection, popupMenu) {
        contextPad.registerProvider(600, this);

        this._modeling = modeling;
        this._elementFactory = elementFactory;
        this._bpmnFactory = bpmnFactory;
        this._selection = selection;
        this._popupMenu = popupMenu;

        this._registerPopupMenuProvider();
    }

    BoundaryEventContextPadProvider.$inject = [
        'contextPad', 'modeling', 'elementFactory', 'bpmnFactory', 'selection', 'popupMenu'
    ];

    BoundaryEventContextPadProvider.prototype._registerPopupMenuProvider = function () {
        var self = this;

        this._popupMenu.registerProvider('boundary-event', {
            getPopupMenuEntries: function (target) {
                return {
                    'attach-boundary-timer': {
                        label: 'Timer Boundary Event',
                        className: 'bpmn-icon-intermediate-event-catch-timer',
                        action: function () {
                            self._createBoundaryEvent(target, 'bpmn:TimerEventDefinition');
                        }
                    },
                    'attach-boundary-message': {
                        label: 'Message Boundary Event',
                        className: 'bpmn-icon-intermediate-event-catch-message',
                        action: function () {
                            self._createBoundaryEvent(target, 'bpmn:MessageEventDefinition');
                        }
                    },
                    'attach-boundary-error': {
                        label: 'Error Boundary Event',
                        className: 'bpmn-icon-intermediate-event-catch-error',
                        action: function () {
                            self._createBoundaryEvent(target, 'bpmn:ErrorEventDefinition');
                        }
                    }
                };
            }
        });
    };

    BoundaryEventContextPadProvider.prototype.getContextPadEntries = function (element) {
        var bo = element.businessObject;
        if (BOUNDARABLE_TYPES.indexOf(bo.$type) === -1) return {};

        var self = this;

        return {
            'attach-boundary-event': {
                group: 'attach',
                className: 'bpmn-icon-boundary-more',
                title: 'Attach Boundary Event',
                action: {
                    click: function (event, element) {
                        self._popupMenu.open(element, 'boundary-event', {
                            x: event.x,
                            y: event.y
                        });
                    }
                }
            }
        };
    };

    BoundaryEventContextPadProvider.prototype._createBoundaryEvent = function (host, eventDefinitionType) {
        var bpmnFactory = this._bpmnFactory;
        var elementFactory = this._elementFactory;
        var modeling = this._modeling;
        var selection = this._selection;

        var eventDefinition = bpmnFactory.create(eventDefinitionType);

        var boundaryEvent = bpmnFactory.create('bpmn:BoundaryEvent', {
            attachedToRef: host.businessObject,
            cancelActivity: true,
            eventDefinitions: [eventDefinition]
        });

        eventDefinition.$parent = boundaryEvent;

        var shape = elementFactory.createShape({
            type: 'bpmn:BoundaryEvent',
            businessObject: boundaryEvent
        });

        var position = {
            x: host.x + host.width,
            y: host.y + host.height
        };

        var created = modeling.createShape(shape, position, host, { attach: true });

        selection.select(created);
    };

    window.boundaryEventContextPadModule = {
        __init__: ['boundaryEventContextPadProvider'],
        boundaryEventContextPadProvider: ['type', BoundaryEventContextPadProvider]
    };
})();
