/**
 * Monitors a specific persistent iframe identified by route.
 * Polls the iframe's src URL with increasing intervals (1s → 5s) until the server responds,
 * then notifies Blazor.
 */
export function monitorIframe(container, route, dotNetRef) {
    const iframe = container.querySelector(`iframe[data-route="${CSS.escape(route)}"]`);
    if (!iframe) {
        return;
    }

    let delay = 1000;
    const maxDelay = 5000;
    const increment = 1000;

    function poll() {
        // HEAD with no-cors: returns an opaque response if the server is listening (any HTTP status),
        // but throws TypeError on network-level failures (connection refused, DNS error, empty response).
        // This distinguishes "server is up but iframe shows error page" from "server isn't running yet".
        fetch(iframe.src, { method: 'HEAD', mode: 'no-cors' })
            .then(() => {
                dotNetRef.invokeMethodAsync('OnIframeHealthy', route);
            })
            .catch(() => {
                // Server not responding yet — increase delay and retry.
                delay = Math.min(delay + increment, maxDelay);
                setTimeout(poll, delay);
            });
    }

    poll();
}

/**
 * Reloads an iframe by resetting its src attribute.
 */
export function reloadIframe(container, route) {
    const iframe = container.querySelector(`iframe[data-route="${CSS.escape(route)}"]`);
    if (iframe) {
        const src = iframe.src;
        iframe.src = '';
        iframe.src = src;
    }
}
