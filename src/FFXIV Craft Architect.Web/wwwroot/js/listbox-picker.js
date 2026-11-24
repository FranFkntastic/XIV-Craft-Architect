// Positions a ListboxPicker menu with fixed positioning so scrolling or clipped
// containers never cut it off, and dismisses it on outside press, scroll or resize.
const handledKeys = new Set(["ArrowUp", "ArrowDown", "Home", "End", "Enter", " ", "Escape"]);
const margin = 8;

export function open(trigger, menu, owner) {
    if (!trigger || !menu) {
        return;
    }

    close(menu);
    const place = () => {
        const anchor = trigger.getBoundingClientRect();
        menu.style.minWidth = `${Math.max(anchor.width, 0)}px`;
        const height = menu.offsetHeight;
        const width = menu.offsetWidth;
        let top = anchor.bottom + 2;
        if (top + height > window.innerHeight - margin && anchor.top - height - 2 >= margin) {
            top = anchor.top - height - 2;
        }

        const left = Math.max(margin, Math.min(anchor.left, window.innerWidth - width - margin));
        menu.style.top = `${Math.round(top)}px`;
        menu.style.left = `${Math.round(left)}px`;
        // A filtered or transformed ancestor (e.g. a glass surface) becomes the containing
        // block for fixed positioning; shift by the measured offset so the menu stays anchored.
        const placed = menu.getBoundingClientRect();
        if (Math.round(placed.top) !== Math.round(top) || Math.round(placed.left) !== Math.round(left)) {
            menu.style.top = `${Math.round(2 * top - placed.top)}px`;
            menu.style.left = `${Math.round(2 * left - placed.left)}px`;
        }
    };
    const dismiss = () => owner.invokeMethodAsync("Dismiss");
    const onPointerDown = event => {
        if (!menu.contains(event.target) && !trigger.contains(event.target)) {
            dismiss();
        }
    };
    const onScroll = event => {
        if (!menu.contains(event.target)) {
            dismiss();
        }
    };
    const onKeyDown = event => {
        if (handledKeys.has(event.key)) {
            event.preventDefault();
        }
    };

    place();
    menu.addEventListener("keydown", onKeyDown);
    document.addEventListener("pointerdown", onPointerDown, true);
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", dismiss);
    menu.focus({ preventScroll: true });
    menu.__listboxPickerCleanup = () => {
        menu.removeEventListener("keydown", onKeyDown);
        document.removeEventListener("pointerdown", onPointerDown, true);
        window.removeEventListener("scroll", onScroll, true);
        window.removeEventListener("resize", dismiss);
        delete menu.__listboxPickerCleanup;
    };
}

export function close(menu) {
    menu?.__listboxPickerCleanup?.();
}

export function reveal(optionId) {
    document.getElementById(optionId)?.scrollIntoView({ block: "nearest" });
}
