const protocolVersion = "2";
const computationResultKind = "computation-result";
let workerGeneration = null;

self.addEventListener("message", event => {
    const message = event.data ?? {};
    if (message.kind === "ping") {
        if (message.protocolVersion !== protocolVersion ||
            !Number.isSafeInteger(message.generation) || message.generation <= 0) {
            return;
        }
        workerGeneration = message.generation;
        self.postMessage({
            protocolVersion,
            kind: "capability",
            generation: workerGeneration,
            executionId: null,
            transactionId: null,
            payload: {
                protocolVersion,
                generation: workerGeneration,
                dedicatedWorker: true,
                crossOriginIsolated: self.crossOriginIsolated === true,
                sharedArrayBufferAvailable: typeof SharedArrayBuffer !== "undefined",
                threadsAvailable: false,
                executionSupported: false,
                resultKind: computationResultKind
            }
        });
        return;
    }

    if (message.kind === "execute" || message.kind === "cancel") {
        if (message.protocolVersion !== protocolVersion ||
            message.generation !== workerGeneration ||
            typeof message.executionId !== "string" || message.executionId.length === 0 ||
            typeof message.transactionId !== "string" || message.transactionId.length === 0) {
            return;
        }
        self.postMessage({
            protocolVersion,
            kind: "protocol-error",
            generation: message.generation,
            executionId: message.executionId,
            transactionId: message.transactionId,
            payload: {
                code: "dotnet-host-not-loaded",
                message: "The static worker exposes the computation-only protocol shape, but .NET compute is not enabled."
            }
        });
    }
});
