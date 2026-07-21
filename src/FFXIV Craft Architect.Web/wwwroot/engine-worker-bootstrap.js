export function createEngineWorker(workerUrl = "engine-worker.js") {
    const worker = new Worker(new URL(workerUrl, document.baseURI), {
        type: "module",
        name: "craft-architect-engine"
    });
    return {
        worker,
        send(message) {
            worker.postMessage(message);
        },
        subscribe(handler) {
            if (typeof handler !== "function") {
                throw new TypeError("A worker message handler is required.");
            }
            const listener = event => handler(event.data);
            worker.addEventListener("message", listener);
            return () => worker.removeEventListener("message", listener);
        },
        ping(generation) {
            if (!Number.isSafeInteger(generation) || generation <= 0) {
                throw new RangeError("A positive worker generation is required.");
            }
            worker.postMessage({
                protocolVersion: "2",
                kind: "ping",
                generation,
                executionId: null,
                transactionId: null,
                payload: null
            });
        },
        terminate() {
            worker.terminate();
        }
    };
}
