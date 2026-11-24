const bindings = new WeakMap();
export function attach(wrap) {
    detach(wrap);
    const scroll = () => {
        const scrolled = wrap.scrollLeft > 0;
        if (wrap.classList.contains('is-scrolled') !== scrolled) wrap.classList.toggle('is-scrolled', scrolled);
    };
    const input = event => {
        const classes = event.target.classList;
        if (!classes || !(classes.contains('sale-input') || classes.contains('set-price-input'))) return;
        let value = event.target.value.replace(/[^0-9]/g, '');
        while (value.length && (value.length > 19 || BigInt(value) > 9223372036854775807n)) value = value.slice(0, -1);
        event.target.value = value;
    };
    wrap.addEventListener('scroll', scroll, { passive: true });
    wrap.addEventListener('input', input, true);
    const observer = new MutationObserver(scroll);
    observer.observe(wrap, { attributes: true, attributeFilter: ['class'] });
    bindings.set(wrap, { scroll, input, observer });
    scroll();
}
// Focuses a material's unit-cost input once the source picker has closed and returned focus
// to its own trigger.
export function focusPrice(wrap, key) {
    let attempts = 0;
    const tryFocus = () => {
        const target = wrap?.querySelector(`.set-price-input[data-price-key="${CSS.escape(key)}"]`);
        if (target) { target.focus(); target.select(); return; }
        if (++attempts < 10) setTimeout(tryFocus, 30);
    };
    requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(tryFocus, 0)));
}
export function detach(wrap) {
    const binding = bindings.get(wrap);
    if (!binding) return;
    binding.observer.disconnect();
    wrap.removeEventListener('scroll', binding.scroll);
    wrap.removeEventListener('input', binding.input, true);
    bindings.delete(wrap);
}
