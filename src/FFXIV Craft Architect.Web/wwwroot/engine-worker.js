const protocolVersion = "1";

self.addEventListener("message", event => {
    const message = event.data ?? {};
    if (message.kind === "ping") {
        self.postMessage({
            protocolVersion,
            kind: "capability",
            transactionId: null,
            payload: {
                protocolVersion,
                dedicatedWorker: true,
                crossOriginIsolated: self.crossOriginIsolated === true,
                sharedArrayBufferAvailable: typeof SharedArrayBuffer !== "undefined",
                threadsAvailable: false
            }
        });
        return;
    }

    if (message.kind === "execute" || message.kind === "cancel") {
        self.postMessage({
            protocolVersion,
            kind: "protocol-error",
            transactionId: message.transactionId ?? null,
            payload: {
                code: "dotnet-host-not-loaded",
                message: "The static worker capability host is ready; .NET engine compute cutover is not enabled."
            }
        });
    }
});
