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

export function createEngineWorkerController(callback, workerUrl = "engine-worker.js") {
    if (!callback || typeof callback.invokeMethodAsync !== "function") {
        throw new TypeError("A .NET Worker callback is required.");
    }
    const controller = createEngineWorker(workerUrl);
    let disposed = false;
    const unsubscribe = controller.subscribe(message => {
        callback.invokeMethodAsync("ReceiveMessage", message).catch(() => {});
    });
    const reportError = event => {
        const message = event?.message ?? "The Worker emitted an unstructured error.";
        callback.invokeMethodAsync("ReceiveError", event.type, message).catch(() => {});
    };
    controller.worker.addEventListener("error", reportError);
    controller.worker.addEventListener("messageerror", reportError);

    function detach() {
        if (disposed) return;
        disposed = true;
        unsubscribe();
        controller.worker.removeEventListener("error", reportError);
        controller.worker.removeEventListener("messageerror", reportError);
    }

    return {
        ping(generation) {
            if (disposed) throw new Error("The Worker controller is disposed.");
            controller.ping(generation);
        },
        send(message) {
            if (disposed) throw new Error("The Worker controller is disposed.");
            controller.send(message);
        },
        terminate() {
            if (disposed) return;
            controller.terminate();
            detach();
        },
        dispose() {
            if (!disposed) controller.terminate();
            detach();
        }
    };
}
