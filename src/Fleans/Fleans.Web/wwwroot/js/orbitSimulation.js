// Editor "Simulation" mode (design board "Simulation mode"): a dry run of the open diagram in the
// browser. Tokens walk the drawn sequence flows; nothing is deployed and no engine is involved, so the
// run shows the *paths* a process can take, not what scripts or conditions would compute:
//   - tasks, call activities, throw events complete after one tick (the speed setting);
//   - user / manual / receive tasks and catch events wait for the user ("Complete" / "Trigger");
//   - exclusive / inclusive / complex gateways with several ways out ask which flow to take (inclusive
//     also offers "all"); event-based gateways ask which event arrives;
//   - parallel gateways join (wait for every incoming flow) and fork; inclusive / complex joins merge;
//   - expanded sub-processes run their own start events and complete when their tokens are gone;
//   - boundary events can be triggered while their host waits (interrupting ones cancel the host);
//   - terminate end events end the whole run.
// The editor is read-only meanwhile (bpmnEditor.setReadOnly); markers, not commands, show progress,
// so the diagram is never dirtied.
(function () {
    var WAITING_TASKS = ['bpmn:UserTask', 'bpmn:ManualTask', 'bpmn:ReceiveTask'];
    var CHOICE_GATEWAYS = ['bpmn:ExclusiveGateway', 'bpmn:InclusiveGateway', 'bpmn:ComplexGateway'];
    var MARKERS = ['orbit-sim-active', 'orbit-sim-done', 'orbit-sim-taken', 'orbit-sim-waiting'];
    var BASE_TICK_MS = 900;

    function name(bo) {
        return bo.name || bo.$type.replace(/^bpmn:/, '').replace(/([a-z])([A-Z])/g, '$1 $2');
    }

    function definitionType(bo) {
        var defs = bo.eventDefinitions || [];
        return defs.length ? defs[0].$type : null;
    }

    function flowLabel(flow) {
        var bo = flow.businessObject;
        if (bo.name) return bo.name;
        if (bo.conditionExpression && bo.conditionExpression.body) return bo.conditionExpression.body;
        return 'to ' + name(flow.target.businessObject);
    }

    function Simulation(modeler, onUpdate) {
        this._modeler = modeler;
        this._onUpdate = onUpdate;
        this._registry = modeler.get('elementRegistry');
        this._canvas = modeler.get('canvas');
        this._overlays = modeler.get('overlays');
        this._speed = 1;
        this._reset();
    }

    Simulation.prototype._reset = function () {
        this._clearTimer();
        this._tokens = [];
        this._nextToken = 1;
        this._step = 0;
        this._log = [];
        this._done = {};
        this._taken = {};
        this._joins = {};
        this._running = false;
        this._finished = false;
        this._startedAt = null;
        this._elapsedMs = 0;
    };

    Simulation.prototype._clearTimer = function () {
        if (this._timer) clearTimeout(this._timer);
        this._timer = null;
    };

    // ---- Public API ---------------------------------------------------------------------------

    Simulation.prototype.start = function () {
        this.reset();
        var root = this._canvas.getRootElement();
        var starts = this._startEventsIn(root);
        if (!starts.length) {
            this._addLog('No start event to run from.', null);
            this._finished = true;
            this._emit();
            return;
        }
        var self = this;
        this._startedAt = Date.now();
        starts.forEach(function (start) { self._spawn(start, null); });
        this._running = true;
        this._schedule();
        this._emit();
    };

    // "Simulate from here" (context menu): one token placed on the given element.
    Simulation.prototype.startFrom = function (elementId) {
        this.reset();
        var element = this._registry.get(elementId);
        if (!element || element.waypoints) {
            this.start();
            return;
        }
        var scopeEl = element.parent;
        var scope = null;
        // Inside an expanded sub-process the token needs its sub-process scope to finish properly.
        if (scopeEl && scopeEl !== this._canvas.getRootElement() && /SubProcess|Transaction/.test(scopeEl.businessObject.$type)) {
            scope = { id: this._nextToken++, element: scopeEl, state: 'inside', scope: null };
            this._tokens.push(scope);
        }
        this._startedAt = Date.now();
        this._spawnAt(element, scope);
        this._addLog('Started at ' + name(element.businessObject), element);
        this._running = true;
        this._schedule();
        this._paint();
        this._emit();
    };

    Simulation.prototype.reset = function () {
        this._reset();
        this._paint();
        this._emit();
    };

    Simulation.prototype.pause = function () {
        this._running = false;
        this._clearTimer();
        this._emit();
    };

    Simulation.prototype.resume = function () {
        if (this._finished) return;
        this._running = true;
        this._schedule();
        this._emit();
    };

    Simulation.prototype.setSpeed = function (speed) {
        this._speed = speed > 0 ? speed : 1;
        if (this._running) this._schedule();
        this._emit();
    };

    // One tick by hand (also what the timer runs).
    Simulation.prototype.step = function () {
        if (this._finished) return;
        var ready = this._tokens.filter(function (t) { return t.state === 'ready'; });
        if (!ready.length) {
            this._emit();
            return;
        }
        this._step++;
        var self = this;
        ready.forEach(function (token) { self._advance(token); });
        this._checkFinished();
        this._paint();
        this._emit();
    };

    // The user's answer for a waiting token: optionId is a flow id (gateway), a boundary event id,
    // an event id after an event-based gateway, 'complete', or 'all' (inclusive gateway).
    Simulation.prototype.choose = function (tokenId, optionId) {
        var token = this._tokens.filter(function (t) { return t.id === tokenId; })[0];
        if (!token || token.state !== 'waiting') return;
        var element = token.element;
        var bo = element.businessObject;
        this._step++;

        var boundary = this._boundariesOf(element).filter(function (b) { return b.id === optionId; })[0];
        if (boundary) {
            this._fireBoundary(token, boundary);
        } else if (CHOICE_GATEWAYS.indexOf(bo.$type) >= 0) {
            var flows = this._outgoing(element);
            var picked = optionId === 'all' ? flows : flows.filter(function (f) { return f.id === optionId; });
            this._addLog(name(bo) + ': ' + picked.map(flowLabel).join(', '), element);
            this._consume(token);
            this._markDone(element);
            var self = this;
            picked.forEach(function (flow) { self._take(flow, token); });
        } else if (bo.$type === 'bpmn:EventBasedGateway') {
            var target = this._outgoing(element).map(function (f) { return f.target; })
                .filter(function (t) { return t.id === optionId; })[0];
            var viaFlow = this._outgoing(element).filter(function (f) { return f.target === target; })[0];
            this._consume(token);
            this._markDone(element);
            if (viaFlow) {
                this._taken[viaFlow.id] = true;
                this._addLog(name(target.businessObject) + ' arrived first', target);
                this._complete(this._spawnAt(target, token.scope), target);
            }
        } else {
            this._addLog(name(bo) + ' completed by the user', element);
            this._complete(token, element);
        }

        this._checkFinished();
        this._paint();
        this._emit();
        if (this._running) this._schedule();
    };

    Simulation.prototype.destroy = function () {
        this._reset();
        this._paint();
    };

    // ---- Token movement -----------------------------------------------------------------------

    Simulation.prototype._schedule = function () {
        this._clearTimer();
        if (!this._running || this._finished) return;
        var self = this;
        this._timer = setTimeout(function () {
            self._timer = null;
            if (!self._running) return;
            self.step();
            self._schedule();
        }, BASE_TICK_MS / this._speed);
    };

    Simulation.prototype._startEventsIn = function (container) {
        var children = container.children || [];
        var direct = children.filter(function (c) {
            return c.businessObject && c.businessObject.$type === 'bpmn:StartEvent' && !c.labelTarget &&
                !(c.businessObject.$parent && c.businessObject.$parent.triggeredByEvent);
        });
        if (direct.length) return direct;
        // A collaboration root: look inside its pools.
        var self = this;
        return children
            .filter(function (c) { return c.businessObject && c.businessObject.$type === 'bpmn:Participant'; })
            .reduce(function (all, pool) { return all.concat(self._startEventsIn(pool)); }, []);
    };

    Simulation.prototype._spawn = function (element, scope) {
        var token = this._spawnAt(element, scope);
        this._addLog(name(element.businessObject) + ' started', element);
        return token;
    };

    Simulation.prototype._spawnAt = function (element, scope) {
        var token = { id: this._nextToken++, element: element, state: 'ready', scope: scope || null };
        this._tokens.push(token);
        return token;
    };

    Simulation.prototype._consume = function (token) {
        token.state = 'gone';
        this._tokens = this._tokens.filter(function (t) { return t !== token; });
    };

    Simulation.prototype._outgoing = function (element) {
        return (element.outgoing || []).filter(function (c) { return c.businessObject.$type === 'bpmn:SequenceFlow'; });
    };

    Simulation.prototype._incoming = function (element) {
        return (element.incoming || []).filter(function (c) { return c.businessObject.$type === 'bpmn:SequenceFlow'; });
    };

    Simulation.prototype._boundariesOf = function (element) {
        return (element.attachers || []).filter(function (b) { return !b.labelTarget; });
    };

    Simulation.prototype._markDone = function (element) {
        this._done[element.id] = true;
    };

    Simulation.prototype._take = function (flow, fromToken) {
        this._taken[flow.id] = true;
        var target = flow.target;
        var bo = target.businessObject;
        if (bo.$type === 'bpmn:ParallelGateway' && this._incoming(target).length > 1) {
            var key = target.id + '#' + (fromToken.scope ? fromToken.scope.id : 'root');
            var join = this._joins[key] || (this._joins[key] = {});
            join[flow.id] = true;
            var needed = this._incoming(target).length;
            if (Object.keys(join).length < needed) {
                this._addLog(name(bo) + ': waiting for ' + (needed - Object.keys(join).length) + ' more branch(es)', target);
                return;
            }
            delete this._joins[key];
            this._addLog(name(bo) + ': all branches arrived', target);
        }
        this._spawnAt(target, fromToken.scope);
    };

    // A ready token does its element's work for one tick.
    Simulation.prototype._advance = function (token) {
        var element = token.element;
        var bo = element.businessObject;
        var type = bo.$type;
        var def = definitionType(bo);

        if (type === 'bpmn:StartEvent') {
            this._complete(token, element);
        } else if (type === 'bpmn:IntermediateThrowEvent' && def === 'bpmn:LinkEventDefinition') {
            // A link throw continues at the catch event with the same link name.
            var linkName = bo.eventDefinitions[0].name;
            var targetCatch = this._registry.filter(function (e) {
                var cbo = e.businessObject;
                return cbo && cbo.$type === 'bpmn:IntermediateCatchEvent' && definitionType(cbo) === 'bpmn:LinkEventDefinition' &&
                    cbo.eventDefinitions[0].name === linkName && !e.labelTarget;
            })[0];
            // Spawn the catch token first so the throw doesn't look like the last token of its scope.
            var catchToken = targetCatch ? this._spawnAt(targetCatch, token.scope) : null;
            this._complete(token, element);
            if (catchToken) this._complete(catchToken, targetCatch);
        } else if (type === 'bpmn:EndEvent') {
            this._markDone(element);
            this._consume(token);
            if (def === 'bpmn:TerminateEventDefinition') {
                this._addLog(name(bo) + ': terminated', element);
                this._tokens.slice().forEach(this._consume, this);
            } else {
                this._addLog(name(bo) + ' reached', element);
            }
            this._completeScopeIfEmpty(token.scope);
        } else if (type === 'bpmn:SubProcess' || type === 'bpmn:Transaction' || type === 'bpmn:AdHocSubProcess') {
            var starts = this._startEventsIn(element);
            if (!starts.length || element.collapsed) {
                this._complete(token, element);
            } else {
                token.state = 'inside';
                this._addLog(name(bo) + ' entered', element);
                var self = this;
                starts.forEach(function (s) { self._spawnAt(s, token); });
            }
        } else if (WAITING_TASKS.indexOf(type) >= 0 ||
            (type === 'bpmn:IntermediateCatchEvent' && def !== 'bpmn:LinkEventDefinition') ||
            (CHOICE_GATEWAYS.indexOf(type) >= 0 && this._outgoing(element).length > 1) ||
            (type === 'bpmn:EventBasedGateway' && this._outgoing(element).length > 0)) {
            token.state = 'waiting';
            this._addLog(name(bo) + ' is waiting', element);
        } else if (this._boundariesOf(element).length && /Task$|^bpmn:CallActivity$/.test(type)) {
            // Give the user a chance to fire a boundary event: hosts with boundaries wait too.
            token.state = 'waiting';
            this._addLog(name(bo) + ' is running', element);
        } else {
            this._complete(token, element);
        }
    };

    Simulation.prototype._complete = function (token, element) {
        var outgoing = this._outgoing(element);
        this._markDone(element);
        this._consume(token);
        if (element.businessObject.$type !== 'bpmn:StartEvent') {
            this._addLog(name(element.businessObject) + ' done', element);
        }
        var self = this;
        // Parallel gateways (and activities with several ways out) fork on every outgoing flow.
        outgoing.forEach(function (flow) { self._take(flow, token); });
        if (!outgoing.length) this._completeScopeIfEmpty(token.scope);
    };

    // The sub-process token (state 'inside') completes once none of its inner tokens is left.
    Simulation.prototype._completeScopeIfEmpty = function (scope) {
        if (!scope || scope.state !== 'inside') return;
        var stillInside = this._tokens.some(function (t) { return t.scope === scope; });
        if (stillInside) return;
        this._complete(scope, scope.element);
    };

    Simulation.prototype._fireBoundary = function (hostToken, boundary) {
        var bo = boundary.businessObject;
        var interrupting = bo.cancelActivity !== false;
        this._addLog(name(bo) + (interrupting ? ' interrupted ' : ' fired on ') + name(hostToken.element.businessObject), boundary);
        if (interrupting) {
            var self = this;
            this._consume(hostToken);
            // Tokens running inside an interrupted sub-process go with it.
            this._tokens.filter(function (t) { return t.scope === hostToken; }).forEach(function (t) { self._consume(t); });
        }
        this._complete(this._spawnAt(boundary, hostToken.scope), boundary);
    };

    Simulation.prototype._checkFinished = function () {
        if (this._tokens.length) return;
        this._finished = true;
        this._running = false;
        this._clearTimer();
        this._addLog('Run finished', null);
    };

    Simulation.prototype._addLog = function (text, element) {
        var at = this._startedAt ? Date.now() - this._startedAt : 0;
        this._elapsedMs = at;
        this._log.push({ step: this._step, atMs: at, text: text, elementId: element ? element.id : null });
        if (this._log.length > 200) this._log.shift();
    };

    // ---- View ---------------------------------------------------------------------------------

    Simulation.prototype._paint = function () {
        var canvas = this._canvas;
        var self = this;
        this._registry.forEach(function (e) {
            MARKERS.forEach(function (m) { canvas.removeMarker(e.id, m); });
        });
        this._overlays.remove({ type: 'orbit-sim' });
        Object.keys(this._done).forEach(function (id) { if (self._registry.get(id)) canvas.addMarker(id, 'orbit-sim-done'); });
        Object.keys(this._taken).forEach(function (id) { if (self._registry.get(id)) canvas.addMarker(id, 'orbit-sim-taken'); });
        this._tokens.forEach(function (t) {
            if (t.state === 'gone') return;
            canvas.addMarker(t.element.id, t.state === 'waiting' ? 'orbit-sim-waiting' : 'orbit-sim-active');
            var dot = document.createElement('span');
            dot.className = 'orbit-sim-token';
            dot.title = 'Token ' + t.id;
            self._overlays.add(t.element.id, 'orbit-sim', { position: { top: -6, left: t.element.width / 2 - 6 }, html: dot });
        });
    };

    Simulation.prototype.snapshot = function () {
        var self = this;
        var pending = this._tokens.filter(function (t) { return t.state === 'waiting'; }).map(function (t) {
            var element = t.element;
            var bo = element.businessObject;
            var options = [];
            if (CHOICE_GATEWAYS.indexOf(bo.$type) >= 0) {
                options = self._outgoing(element).map(function (f) {
                    return { id: f.id, label: flowLabel(f).slice(0, 60), primary: false };
                });
                if (bo.$type === 'bpmn:InclusiveGateway') options.push({ id: 'all', label: 'All branches', primary: false });
            } else if (bo.$type === 'bpmn:EventBasedGateway') {
                options = self._outgoing(element).map(function (f) {
                    return { id: f.target.id, label: name(f.target.businessObject).slice(0, 60), primary: false };
                });
            } else {
                options.push({ id: 'complete', label: bo.$type === 'bpmn:IntermediateCatchEvent' ? 'Trigger' : 'Complete', primary: true });
            }
            self._boundariesOf(element).forEach(function (b) {
                options.push({ id: b.id, label: 'Fire ' + name(b.businessObject).slice(0, 50), primary: false });
            });
            return {
                tokenId: t.id,
                elementId: element.id,
                elementName: name(bo).slice(0, 60),
                elementType: bo.$type,
                options: options
            };
        });
        return {
            running: this._running,
            finished: this._finished,
            started: this._startedAt !== null,
            step: this._step,
            elapsedMs: this._elapsedMs,
            speed: this._speed,
            activeTokens: this._tokens.length,
            visited: Object.keys(this._done).length,
            pending: pending.slice(0, 20),
            log: this._log.slice(-40)
        };
    };

    Simulation.prototype._emit = function () {
        if (this._onUpdate) this._onUpdate(this.snapshot());
    };

    window.OrbitSimulation = Simulation;
})();
