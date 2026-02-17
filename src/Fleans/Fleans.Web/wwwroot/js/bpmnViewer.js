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
        this._viewer = new BpmnViewer({ container: container });

        try {
            await this._viewer.importXML(bpmnXml);
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

    highlight: function (completedIds, activeIds) {
        if (!this._viewer) return;

        const canvas = this._viewer.get('canvas');
        const elementRegistry = this._viewer.get('elementRegistry');

        elementRegistry.forEach(function (element) {
            canvas.removeMarker(element.id, 'bpmn-completed');
            canvas.removeMarker(element.id, 'bpmn-active');
            canvas.removeMarker(element.id, 'bpmn-selected');
        });

        if (completedIds) {
            completedIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-completed');
                }
            });
        }

        if (activeIds) {
            activeIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-active');
                }
            });
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

        var bo = element.businessObject;
        var data = {
            id: bo.id || '',
            type: element.type || '',
            name: bo.name || '',
            scriptFormat: bo.scriptFormat || '',
            script: bo.script || '',
            conditionExpression: (bo.conditionExpression && bo.conditionExpression.body) || '',
            timerType: '',
            timerExpression: '',
            hasTimerDefinition: false
        };

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
            }
        }

        return data;
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
