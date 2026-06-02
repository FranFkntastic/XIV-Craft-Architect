const registeredRoots = new WeakSet();

export function registerResizableGrid(root) {
    if (!root || registeredRoots.has(root)) {
        return;
    }

    registeredRoots.add(root);

    root.addEventListener("pointerdown", event => {
        const handle = event.target.closest("[data-web-table-resize-index]");
        if (!handle || !root.contains(handle)) {
            return;
        }

        const columnIndex = Number.parseInt(handle.dataset.webTableResizeIndex ?? "-1", 10);
        if (columnIndex < 0) {
            return;
        }

        const widths = parseNumberList(root.dataset.columnWidths);
        const minWidths = parseNumberList(root.dataset.columnMinWidths);
        if (columnIndex >= widths.length) {
            return;
        }

        event.preventDefault();
        handle.setPointerCapture(event.pointerId);

        const startX = event.clientX;
        const startWidth = widths[columnIndex];
        const minWidth = minWidths[columnIndex] || 80;

        const onPointerMove = moveEvent => {
            const delta = moveEvent.clientX - startX;
            widths[columnIndex] = Math.max(minWidth, Math.round(startWidth + delta));
            applyGridWidths(root, widths);
        };

        const onPointerUp = upEvent => {
            handle.releasePointerCapture(upEvent.pointerId);
            handle.removeEventListener("pointermove", onPointerMove);
            handle.removeEventListener("pointerup", onPointerUp);
            handle.removeEventListener("pointercancel", onPointerUp);
            root.dataset.columnWidths = widths.join(",");
        };

        handle.addEventListener("pointermove", onPointerMove);
        handle.addEventListener("pointerup", onPointerUp);
        handle.addEventListener("pointercancel", onPointerUp);
    });
}

function parseNumberList(value) {
    if (!value) {
        return [];
    }

    return value
        .split(",")
        .map(item => Number.parseInt(item, 10))
        .filter(item => Number.isFinite(item));
}

function applyGridWidths(root, widths) {
    const gap = readPixelVariable(root, "--web-table-grid-gap");
    const template = widths.map(width => `${width}px`).join(" ");
    const totalWidth = widths.reduce((sum, width) => sum + width, 0) +
        Math.max(0, widths.length - 1) * gap +
        14;

    root.style.setProperty("--web-table-grid-template", template);
    root.style.setProperty("--web-table-grid-width", `${totalWidth}px`);
}

function readPixelVariable(root, name) {
    const value = getComputedStyle(root).getPropertyValue(name).trim();
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) ? parsed : 0;
}
