export function createEngineWorker(workerUrl = "engine-worker.js") {
    const worker = new Worker(workerUrl, { type: "classic", name: "craft-architect-engine" });
    return {
        worker,
        ping() {
            worker.postMessage({ protocolVersion: "1", kind: "ping", transactionId: null, payload: null });
        },
        terminate() {
            worker.terminate();
        }
    };
}
