export function registerTradeOrdersLayout(
    board,
    splitter,
    dotNetReference,
    initialWidth,
    minimumWidth,
    maximumWidth) {
    if (!board || !splitter) {
        return { dispose() {} };
    }

    let currentWidth = clampWidth(initialWidth);
    let activePointerId = null;
    let startX = 0;
    let startWidth = currentWidth;

    applyWidth(currentWidth);

    const onPointerDown = event => {
        if (event.button !== 0) {
            return;
        }

        event.preventDefault();
        activePointerId = event.pointerId;
        startX = event.clientX;
        startWidth = currentWidth;
        splitter.setPointerCapture(activePointerId);
        board.classList.add("is-resizing-ops");
    };

    const onPointerMove = event => {
        if (activePointerId !== event.pointerId) {
            return;
        }

        applyWidth(startWidth + startX - event.clientX);
    };

    const finishPointerResize = event => {
        if (activePointerId !== event.pointerId) {
            return;
        }

        if (splitter.hasPointerCapture(activePointerId)) {
            splitter.releasePointerCapture(activePointerId);
        }
        activePointerId = null;
        board.classList.remove("is-resizing-ops");
        persistWidth();
    };

    const onKeyDown = event => {
        let nextWidth = null;
        if (event.key === "ArrowLeft") {
            nextWidth = currentWidth + 20;
        } else if (event.key === "ArrowRight") {
            nextWidth = currentWidth - 20;
        } else if (event.key === "Home") {
            nextWidth = minimumWidth;
        } else if (event.key === "End") {
            nextWidth = maximumWidth;
        }

        if (nextWidth == null) {
            return;
        }

        event.preventDefault();
        applyWidth(nextWidth);
        persistWidth();
    };

    function clampWidth(width) {
        const numericWidth = Number(width);
        if (!Number.isFinite(numericWidth)) {
            return minimumWidth;
        }

        return Math.max(minimumWidth, Math.min(maximumWidth, Math.round(numericWidth)));
    }

    function applyWidth(width) {
        currentWidth = clampWidth(width);
        board.style.setProperty("--trade-orders-ops-width", `${currentWidth}px`);
        splitter.setAttribute("aria-valuenow", currentWidth.toString());
    }

    function persistWidth() {
        dotNetReference.invokeMethodAsync(
            "SaveTradeOrdersOpsPaneWidthAsync",
            currentWidth);
    }

    splitter.addEventListener("pointerdown", onPointerDown);
    splitter.addEventListener("pointermove", onPointerMove);
    splitter.addEventListener("pointerup", finishPointerResize);
    splitter.addEventListener("pointercancel", finishPointerResize);
    splitter.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            splitter.removeEventListener("pointerdown", onPointerDown);
            splitter.removeEventListener("pointermove", onPointerMove);
            splitter.removeEventListener("pointerup", finishPointerResize);
            splitter.removeEventListener("pointercancel", finishPointerResize);
            splitter.removeEventListener("keydown", onKeyDown);
            board.classList.remove("is-resizing-ops");
        }
    };
}
