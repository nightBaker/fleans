window.dragResize = {
    /**
     * Attach a vertical drag-resize handler.
     * @param {string} handleId - The drag handle element ID
     * @param {string} topId - The top panel element ID (gets height adjusted)
     * @param {number} minHeight - Minimum height in px
     * @param {number} maxHeight - Maximum height in px
     * @param {object} dotNetRef - Blazor .NET reference for callbacks
     * @param {string} callbackMethod - Method name to invoke on drag end with new height
     */
    getViewportHeight: function () {
        return window.innerHeight;
    },

    initVertical: function (handleId, topId, minHeight, maxHeight, dotNetRef, callbackMethod) {
        var handle = document.getElementById(handleId);
        var topPanel = document.getElementById(topId);
        if (!handle || !topPanel) return;

        var startY = 0;
        var startHeight = 0;

        function onMouseDown(e) {
            e.preventDefault();
            startY = e.clientY;
            startHeight = topPanel.getBoundingClientRect().height;
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'row-resize';
            document.body.style.userSelect = 'none';
        }

        function onMouseMove(e) {
            var delta = e.clientY - startY;
            var newHeight = Math.min(Math.max(startHeight + delta, minHeight), maxHeight);
            topPanel.style.height = newHeight + 'px';
        }

        function onMouseUp(e) {
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            var finalHeight = topPanel.getBoundingClientRect().height;
            if (dotNetRef && callbackMethod) {
                dotNetRef.invokeMethodAsync(callbackMethod, finalHeight);
            }
        }

        if (handle._dragResizeHandler) {
            handle.removeEventListener('mousedown', handle._dragResizeHandler);
        }
        handle._dragResizeHandler = onMouseDown;
        handle.addEventListener('mousedown', onMouseDown);
    },

    /**
     * Attach a horizontal drag-resize handler.
     * @param {string} handleId - The drag handle element ID
     * @param {string} panelId - The right panel element ID (gets width adjusted)
     * @param {number} minWidth - Minimum width in px
     * @param {number} maxWidth - Maximum width in px
     * @param {object} dotNetRef - Blazor .NET reference for callbacks
     * @param {string} callbackMethod - Method name to invoke on drag end with new width
     */
    initHorizontal: function (handleId, panelId, minWidth, maxWidth, dotNetRef, callbackMethod) {
        var handle = document.getElementById(handleId);
        var panel = document.getElementById(panelId);
        if (!handle || !panel) return;

        var startX = 0;
        var startWidth = 0;

        function onMouseDown(e) {
            e.preventDefault();
            startX = e.clientX;
            startWidth = panel.getBoundingClientRect().width;
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        }

        function onMouseMove(e) {
            // Dragging left increases panel width (panel is on the right)
            var delta = startX - e.clientX;
            var newWidth = Math.min(Math.max(startWidth + delta, minWidth), maxWidth);
            panel.style.width = newWidth + 'px';
        }

        function onMouseUp(e) {
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            var finalWidth = panel.getBoundingClientRect().width;
            if (dotNetRef && callbackMethod) {
                dotNetRef.invokeMethodAsync(callbackMethod, finalWidth);
            }
        }

        if (handle._dragResizeHandler) {
            handle.removeEventListener('mousedown', handle._dragResizeHandler);
        }
        handle._dragResizeHandler = onMouseDown;
        handle.addEventListener('mousedown', onMouseDown);
    }
};
