(function () {
    var BOUNDARABLE_TYPES = [
        'bpmn:Task',
        'bpmn:UserTask',
        'bpmn:ServiceTask',
        'bpmn:ScriptTask',
        'bpmn:CallActivity',
        'bpmn:SubProcess'
    ];

    function BoundaryEventContextPadProvider(contextPad, modeling, elementFactory, bpmnFactory, selection) {
        contextPad.registerProvider(600, this);

        this._modeling = modeling;
        this._elementFactory = elementFactory;
        this._bpmnFactory = bpmnFactory;
        this._selection = selection;
    }

    BoundaryEventContextPadProvider.$inject = [
        'contextPad', 'modeling', 'elementFactory', 'bpmnFactory', 'selection'
    ];

    BoundaryEventContextPadProvider.prototype.getContextPadEntries = function (element) {
        var bo = element.businessObject;
        if (BOUNDARABLE_TYPES.indexOf(bo.$type) === -1) return {};

        var self = this;

        return {
            'attach-boundary-timer': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-timer',
                title: 'Attach Timer Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:TimerEventDefinition');
                    }
                }
            },
            'attach-boundary-message': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-message',
                title: 'Attach Message Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:MessageEventDefinition');
                    }
                }
            },
            'attach-boundary-error': {
                group: 'attach',
                className: 'bpmn-icon-intermediate-event-catch-error',
                title: 'Attach Error Boundary Event',
                action: {
                    click: function (event, element) {
                        self._createBoundaryEvent(element, 'bpmn:ErrorEventDefinition');
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
            cancelActivity: eventDefinitionType !== 'bpmn:ErrorEventDefinition',
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
