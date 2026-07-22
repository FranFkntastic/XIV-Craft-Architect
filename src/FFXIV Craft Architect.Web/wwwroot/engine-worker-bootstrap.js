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
                protocolVersion: "4",
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
        if (message?.kind === "managed-json" && typeof message.messageJson === "string") {
            if (message.messageKind === "progress") {
                window.dispatchEvent(new Event("craft-architect-engine-worker-progress"));
            } else if (message.messageKind === "computation-result") {
                window.dispatchEvent(new Event("craft-architect-engine-worker-complete"));
            }
            callback.invokeMethodAsync("ReceiveMessageJson", message.messageJson).catch(() => {});
            return;
        }
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
        sendJson(messageJson, generation, kind) {
            if (disposed) throw new Error("The Worker controller is disposed.");
            if (typeof messageJson !== "string" || messageJson.length === 0 ||
                !Number.isSafeInteger(generation) || generation <= 0 ||
                (kind !== "execute" && kind !== "cancel")) {
                throw new TypeError("A valid managed Worker JSON message is required.");
            }
            const identity = JSON.parse(messageJson);
            if (identity?.protocolVersion !== "4" ||
                identity?.generation !== generation ||
                typeof identity?.executionId !== "string" ||
                typeof identity?.transactionId !== "string") {
                throw new TypeError("Managed Worker JSON identity is invalid.");
            }
            controller.send({
                kind: "managed-json",
                messageJson,
                generation,
                messageKind: kind,
                executionId: identity.executionId,
                transactionId: identity.transactionId
            });
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

export function reportEngineHostFinalized() {
    window.dispatchEvent(new Event("craft-architect-engine-host-finalized"));
}

export function yieldToBrowser() {
    return new Promise(resolve => setTimeout(resolve, 0));
}
