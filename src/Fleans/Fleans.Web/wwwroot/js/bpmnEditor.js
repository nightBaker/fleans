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
            keyboard: { bindTo: container }
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
            conditionExpression: ''
        };

        if (bo.$type === 'bpmn:ScriptTask') {
            data.scriptFormat = bo.scriptFormat || '';
            data.script = bo.script || '';
        }

        if (bo.$type === 'bpmn:SequenceFlow' && bo.conditionExpression) {
            data.conditionExpression = bo.conditionExpression.body || '';
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
        } else {
            props[propertyName] = value;
        }

        modeling.updateProperties(element, props);
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

    loadXml: async function (bpmnXml) {
        if (!this._modeler) return;

        try {
            await this._modeler.importXML(bpmnXml);
            var canvas = this._modeler.get('canvas');
            canvas.zoom('fit-viewport');
        } catch (err) {
            console.error('Failed to load BPMN XML', err);
            throw err;
        }
    },

    newDiagram: async function () {
        var xml = '<?xml version="1.0" encoding="UTF-8"?>' +
            '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" ' +
            'xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI" ' +
            'xmlns:dc="http://www.omg.org/spec/DD/20100524/DC" ' +
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
    }
};
