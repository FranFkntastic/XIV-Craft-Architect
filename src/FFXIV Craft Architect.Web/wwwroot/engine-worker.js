const protocolVersion = "2";
const computationResultKind = "computation-result";
const runtimeProofChallenge = "craft-architect-engine-worker-v1";
let workerGeneration = null;

self.onmessage = async event => {
    const message = event.data ?? {};
    if (message.kind === "ping") {
        if (message.protocolVersion !== protocolVersion ||
            !Number.isSafeInteger(message.generation) || message.generation <= 0) {
            return;
        }

        const requestedGeneration = message.generation;
        workerGeneration = requestedGeneration;
        const managedRuntime = await managedRuntimePromise;
        if (workerGeneration !== requestedGeneration) {
            return;
        }
        self.postMessage({
            protocolVersion,
            kind: "capability",
            generation: requestedGeneration,
            executionId: null,
            transactionId: null,
            payload: {
                protocolVersion,
                generation: requestedGeneration,
                dedicatedWorker: true,
                crossOriginIsolated: self.crossOriginIsolated === true,
                sharedArrayBufferAvailable: typeof SharedArrayBuffer !== "undefined",
                threadsAvailable: false,
                executionSupported: false,
                resultKind: computationResultKind,
                managedRuntimeReady: managedRuntime.ready,
                managedRuntimeAssembly: managedRuntime.proof?.runtimeAssembly ?? null,
                managedRuntimeProofHash: managedRuntime.proof?.proofHash ?? null
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
                code: "engine-execution-not-enabled",
                message: "The managed worker runtime is ready, but engine execution remains disabled until its bounded compute host is integrated."
            }
        });
    }
};

const managedRuntimePromise = initializeManagedRuntime();

async function initializeManagedRuntime() {
    try {
        const dotnetUrl = new URL("./_framework/dotnet.js", import.meta.url);
        const configUrl = new URL("./_framework/blazor.boot.json", import.meta.url);
        const { dotnet } = await import(dotnetUrl.href);
        const runtime = await dotnet
            .withConfigSrc(configUrl.href)
            .create();
        const config = runtime.getConfig();
        const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
        const proofJson = exports.CraftArchitectEngineWorker.ManagedHost.GetRuntimeProofJson(runtimeProofChallenge);
        const proof = JSON.parse(proofJson);
        if (proof.protocolVersion !== protocolVersion ||
            proof.challenge !== runtimeProofChallenge ||
            typeof proof.runtimeAssembly !== "string" || proof.runtimeAssembly.length === 0 ||
            typeof proof.challengeHash !== "string" || !/^[0-9a-f]{64}$/i.test(proof.challengeHash) ||
            typeof proof.proofHash !== "string" || !/^[0-9a-f]{64}$/i.test(proof.proofHash)) {
            throw new Error("The managed worker runtime returned an invalid self-check proof.");
        }
        return { ready: true, runtime, proof };
    } catch (error) {
        console.error("Managed engine worker startup failed.", error);
        return { ready: false, error: String(error) };
    }
}
