const bindings = new WeakMap();
export function attach(wrap) {
    detach(wrap);
    const scroll = () => {
        const scrolled = wrap.scrollLeft > 0;
        if (wrap.classList.contains('is-scrolled') !== scrolled) wrap.classList.toggle('is-scrolled', scrolled);
    };
    const input = event => {
        if (!event.target.classList.contains('sale-input')) return;
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
export function detach(wrap) {
    const binding = bindings.get(wrap);
    if (!binding) return;
    binding.observer.disconnect();
    wrap.removeEventListener('scroll', binding.scroll);
    wrap.removeEventListener('input', binding.input, true);
    bindings.delete(wrap);
}
