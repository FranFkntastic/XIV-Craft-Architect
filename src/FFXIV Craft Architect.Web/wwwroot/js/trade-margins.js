const bindings = new WeakMap();
export function attach(wrap) {
    const scroll = () => wrap.classList.toggle('is-scrolled', wrap.scrollLeft > 0);
    const input = event => {
        if (!event.target.classList.contains('sale-input')) return;
        let value = event.target.value.replace(/[^0-9]/g, '');
        while (value.length && (value.length > 19 || BigInt(value) > 9223372036854775807n)) value = value.slice(0, -1);
        event.target.value = value;
    };
    wrap.addEventListener('scroll', scroll, { passive: true });
    wrap.addEventListener('input', input, true);
    bindings.set(wrap, { scroll, input });
    scroll();
}
export function detach(wrap) {
    const binding = bindings.get(wrap);
    if (!binding) return;
    wrap.removeEventListener('scroll', binding.scroll);
    wrap.removeEventListener('input', binding.input, true);
    bindings.delete(wrap);
}
