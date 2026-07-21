export function createEngineWorker(workerUrl = "engine-worker.js") {
    const worker = new Worker(workerUrl, { type: "classic", name: "craft-architect-engine" });
    return {
        worker,
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
