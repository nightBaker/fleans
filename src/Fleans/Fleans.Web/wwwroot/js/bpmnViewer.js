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

    highlight: function (activeIds, errorIds, lastErrorId) {
        if (!this._viewer) return;

        const canvas = this._viewer.get('canvas');
        const elementRegistry = this._viewer.get('elementRegistry');

        elementRegistry.forEach(function (element) {
            canvas.removeMarker(element.id, 'bpmn-completed');
            canvas.removeMarker(element.id, 'bpmn-active');
            canvas.removeMarker(element.id, 'bpmn-error');
            canvas.removeMarker(element.id, 'bpmn-error-pulse');
            canvas.removeMarker(element.id, 'bpmn-selected');
        });

        if (activeIds) {
            activeIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-active');
                }
            });
        }

        if (errorIds) {
            errorIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, id === lastErrorId ? 'bpmn-error-pulse' : 'bpmn-error');
                }
            });
        }
    },

    highlightExecutionPath: function (orderedActivityIds) {
        if (!this._viewer) return;

        var canvas = this._viewer.get('canvas');
        var elementRegistry = this._viewer.get('elementRegistry');

        // Get or create the execution-path layer (above default layer)
        var layer = canvas.getLayer('executionPath', 1);

        // Clear previous overlays
        while (layer.firstChild) {
            layer.removeChild(layer.firstChild);
        }

        if (!orderedActivityIds || orderedActivityIds.length < 2) return;

        // Build map: "sourceId→targetId" -> element
        var flowMap = {};
        elementRegistry.forEach(function (el) {
            if (el.type === 'bpmn:SequenceFlow' && el.businessObject.sourceRef && el.businessObject.targetRef) {
                var key = el.businessObject.sourceRef.id + '\u2192' + el.businessObject.targetRef.id;
                flowMap[key] = el;
            }
        });

        // Count traversals per sequence flow
        var flowCounts = {};
        for (var i = 1; i < orderedActivityIds.length; i++) {
            var target = orderedActivityIds[i];
            for (var j = i - 1; j >= 0; j--) {
                var source = orderedActivityIds[j];
                var key = source + '\u2192' + target;
                if (flowMap[key]) {
                    var flowId = flowMap[key].id;
                    flowCounts[flowId] = (flowCounts[flowId] || 0) + 1;
                    break;
                }
            }
        }

        // Add arrowhead marker definition
        var svgRoot = layer.ownerSVGElement;
        var defs = svgRoot.querySelector('defs');
        if (!defs) {
            defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
            svgRoot.insertBefore(defs, svgRoot.firstChild);
        }

        // Remove old marker if present, then add fresh
        var oldMarker = defs.querySelector('#execution-arrow');
        if (oldMarker) oldMarker.remove();

        var marker = document.createElementNS('http://www.w3.org/2000/svg', 'marker');
        marker.setAttribute('id', 'execution-arrow');
        marker.setAttribute('viewBox', '0 0 10 10');
        marker.setAttribute('refX', '10');
        marker.setAttribute('refY', '5');
        marker.setAttribute('markerWidth', '5');
        marker.setAttribute('markerHeight', '5');
        marker.setAttribute('orient', 'auto');
        var arrowPath = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        arrowPath.setAttribute('d', 'M 0 0 L 10 5 L 0 10 Z');
        arrowPath.classList.add('execution-arrow-fill');
        marker.appendChild(arrowPath);
        defs.appendChild(marker);

        // Draw overlay paths — one offset line per traversal
        var STRIDE = 3; // px between parallel lines
        var flowIds = Object.keys(flowCounts);
        for (var f = 0; f < flowIds.length; f++) {
            var fId = flowIds[f];
            var count = flowCounts[fId];
            var element = elementRegistry.get(fId);
            if (!element || !element.waypoints || element.waypoints.length < 2) continue;

            for (var t = 0; t < count; t++) {
                var offset = (t + 1) * STRIDE;
                var offsetWaypoints = this._offsetWaypoints(element.waypoints, offset);

                var d = offsetWaypoints.map(function (wp, idx) {
                    return (idx === 0 ? 'M' : 'L') + wp.x + ',' + wp.y;
                }).join(' ');

                var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                path.setAttribute('d', d);
                path.setAttribute('fill', 'none');
                path.setAttribute('marker-end', 'url(#execution-arrow)');
                path.classList.add('execution-path-line');

                layer.appendChild(path);
            }
        }
    },

    // Compute waypoints offset perpendicular to the path direction
    _offsetWaypoints: function (waypoints, offset) {
        if (offset === 0) return waypoints;

        var result = [];
        for (var i = 0; i < waypoints.length; i++) {
            var nx = 0, ny = 0;

            if (i === 0) {
                // First point: use normal of first segment
                var seg = this._segmentNormal(waypoints[0], waypoints[1]);
                nx = seg.nx; ny = seg.ny;
            } else if (i === waypoints.length - 1) {
                // Last point: use normal of last segment
                var seg = this._segmentNormal(waypoints[i - 1], waypoints[i]);
                nx = seg.nx; ny = seg.ny;
            } else {
                // Middle point: average normals of adjacent segments
                var s1 = this._segmentNormal(waypoints[i - 1], waypoints[i]);
                var s2 = this._segmentNormal(waypoints[i], waypoints[i + 1]);
                nx = (s1.nx + s2.nx) / 2;
                ny = (s1.ny + s2.ny) / 2;
                var len = Math.sqrt(nx * nx + ny * ny);
                if (len > 0) { nx /= len; ny /= len; }
            }

            result.push({ x: waypoints[i].x + nx * offset, y: waypoints[i].y + ny * offset });
        }
        return result;
    },

    // Get the unit normal (perpendicular) vector for a segment
    _segmentNormal: function (p1, p2) {
        var dx = p2.x - p1.x;
        var dy = p2.y - p1.y;
        var len = Math.sqrt(dx * dx + dy * dy);
        if (len === 0) return { nx: 0, ny: 0 };
        return { nx: -dy / len, ny: dx / len };
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
            hasTimerDefinition: false,
            hasMessageDefinition: false,
            messageName: '',
            correlationKey: ''
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
                if (bo.eventDefinitions[i].$type === 'bpmn:MessageEventDefinition') {
                    data.hasMessageDefinition = true;
                    var msgDef = bo.eventDefinitions[i];
                    if (msgDef.messageRef) {
                        data.messageName = msgDef.messageRef.name || '';
                    }
                    var attrs = bo.$attrs || {};
                    data.correlationKey = attrs['fleans:correlationKey'] || attrs['zeebe:correlationKey'] || '';
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
