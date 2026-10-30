// Inspector resizing is local UI state: no BPMN commands or SignalR calls while dragging.
(function () {
    var cleanup = null;

    window.fleansEditorInspector = {
        bind: function (rootId) {
            this.destroy();
            var root = document.getElementById(rootId);
            if (!root) return;
            var content = root.querySelector('.editor-content');
            var inspector = root.querySelector('.editor-inspector');
            var handle = root.querySelector('.editor-inspector-resizer');
            if (!content || !inspector || !handle) return;

            var events = new AbortController();
            var options = { signal: events.signal };
            var mobile = window.matchMedia('(max-width: 768px)');
            var preferredWidth = null;
            var drag = null;
            var frame = null;

            function bounds() {
                var rail = root.querySelector('.editor-left');
                var railWidth = rail ? rail.getBoundingClientRect().width : 0;
                // Keep a useful canvas beside the rail and inspector on desktop.
                return { min: 280, max: Math.max(280, Math.min(640, content.clientWidth - railWidth - 320)) };
            }

            function updateAria(width, limits) {
                handle.setAttribute('aria-valuemin', limits.min);
                handle.setAttribute('aria-valuemax', Math.round(limits.max));
                handle.setAttribute('aria-valuenow', Math.round(width));
                handle.setAttribute('aria-valuetext', Math.round(width) + ' pixels');
            }

            function applyWidth(width) {
                var limits = bounds();
                width = Math.round(Math.max(limits.min, Math.min(limits.max, width)));
                root.style.setProperty('--editor-inspector-width', width + 'px');
                updateAria(width, limits);
                // Resize bpmn-js at most once per frame; never fit/zoom the user's diagram.
                if (frame === null) {
                    frame = requestAnimationFrame(function () {
                        frame = null;
                        var canvas = root.querySelector('.editor-canvas');
                        if (canvas && canvas.clientWidth && canvas.clientHeight) window.bpmnEditor.refreshCanvas();
                    });
                }
                return width;
            }

            function stopDrag(event) {
                if (!drag || (event && event.pointerId !== drag.id)) return;
                var pointerId = drag.id;
                drag = null;
                root.classList.remove('editor-page--resizing');
                if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
            }

            handle.addEventListener('pointerdown', function (event) {
                if (mobile.matches || !event.isPrimary || event.button !== 0 || drag) return;
                event.preventDefault();
                handle.focus({ preventScroll: true });
                drag = { id: event.pointerId, x: event.clientX, width: inspector.getBoundingClientRect().width };
                handle.setPointerCapture(event.pointerId);
                root.classList.add('editor-page--resizing');
            }, options);

            handle.addEventListener('pointermove', function (event) {
                if (!drag || event.pointerId !== drag.id) return;
                preferredWidth = applyWidth(drag.width + drag.x - event.clientX);
            }, options);
            handle.addEventListener('pointerup', stopDrag, options);
            handle.addEventListener('pointercancel', stopDrag, options);
            handle.addEventListener('lostpointercapture', stopDrag, options);
            window.addEventListener('blur', function () { stopDrag(); }, options);

            handle.addEventListener('keydown', function (event) {
                if (mobile.matches) return;
                var width = inspector.getBoundingClientRect().width;
                switch (event.key) {
                    case 'ArrowLeft': width += 16; break;
                    case 'ArrowRight': width -= 16; break;
                    case 'Home': width = bounds().min; break;
                    case 'End': width = bounds().max; break;
                    default: return;
                }
                event.preventDefault();
                event.stopPropagation();
                preferredWidth = applyWidth(width);
            }, options);

            var observer = new ResizeObserver(function () {
                if (mobile.matches) {
                    stopDrag();
                    return;
                }
                if (preferredWidth !== null) applyWidth(preferredWidth);
                else updateAria(inspector.getBoundingClientRect().width, bounds());
            });
            observer.observe(content);

            cleanup = function () {
                stopDrag();
                events.abort();
                observer.disconnect();
                if (frame !== null) cancelAnimationFrame(frame);
            };
        },

        destroy: function () {
            if (cleanup) cleanup();
            cleanup = null;
        }
    };
})();
