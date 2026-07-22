const protocolVersion = "4";
const computationResultKind = "computation-result";
const runtimeProofChallenge = "craft-architect-engine-worker-v1";
const workerInstanceId = crypto.randomUUID();
const acceptanceMode = new URL(import.meta.url).searchParams.get("acceptance") === "true";
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
                executionSupported: managedRuntime.ready,
                resultKind: computationResultKind,
                managedRuntimeReady: managedRuntime.ready,
                managedRuntimeAssembly: managedRuntime.proof?.runtimeAssembly ?? null,
                managedRuntimeProofHash: managedRuntime.proof?.proofHash ?? null,
                workerInstanceId
            }
        });
        return;
    }

    if (message.kind === "acceptance-request" && acceptanceMode) {
        const managedRuntime = await managedRuntimePromise;
        if (!managedRuntime.ready || message.generation !== workerGeneration) {
            return;
        }
        self.postMessage({
            protocolVersion,
            kind: "acceptance-request",
            generation: message.generation,
            executionId: null,
            transactionId: null,
            messageJson: managedRuntime.host.GetAcceptanceExecuteMessageJson(message.generation)
        });
        return;
    }

    if (message.kind === "acceptance-hang" && acceptanceMode) {
        const managedRuntime = await managedRuntimePromise;
        self.postMessage({
            protocolVersion,
            kind: "acceptance-hang-started",
            generation: message.generation,
            executionId: null,
            transactionId: null,
            payload: { workerInstanceId }
        });
        managedRuntime.host.RunAcceptanceHang();
        return;
    }

    if (message.kind === "managed-json") {
        if (typeof message.messageJson !== "string" || message.messageJson.length === 0 ||
            message.generation !== workerGeneration ||
            (message.messageKind !== "execute" && message.messageKind !== "cancel")) {
            return;
        }
        let identity;
        try {
            identity = JSON.parse(message.messageJson);
        } catch (error) {
            postProtocolError(message, "managed-json-invalid", String(error));
            return;
        }
        const managedRuntime = await managedRuntimePromise;
        if (!managedRuntime.ready) {
            postProtocolError(identity, "dotnet-host-not-loaded", "The managed engine Worker failed to start.");
            return;
        }
        if (message.messageKind === "execute") {
            dispatchManagedExecutionJson(message.messageJson, identity, managedRuntime.host);
            return;
        }
        try {
            managedRuntime.host.CancelMessageJson(message.messageJson);
        } catch (error) {
            postProtocolError(identity, "managed-cancel-rejected", String(error));
        }
        return;
    }

    if (message.kind !== "execute" && message.kind !== "cancel") {
        return;
    }
    if (message.protocolVersion !== protocolVersion ||
        message.generation !== workerGeneration ||
        typeof message.executionId !== "string" || message.executionId.length === 0 ||
        typeof message.transactionId !== "string" || message.transactionId.length === 0) {
        return;
    }

    const managedRuntime = await managedRuntimePromise;
    if (!managedRuntime.ready) {
        postProtocolError(message, "dotnet-host-not-loaded", "The managed engine Worker failed to start.");
        return;
    }
    if (message.kind === "execute") {
        dispatchManagedExecution(message, managedRuntime.host);
        return;
    }
    try {
        managedRuntime.host.CancelMessageJson(JSON.stringify(message));
    } catch (error) {
        postProtocolError(message, "managed-cancel-rejected", String(error));
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
        runtime.setModuleImports("engine-worker", {
            postMessage(messageJson) {
                self.postMessage({
                    kind: "managed-json",
                    messageJson,
                    messageKind: JSON.parse(messageJson).kind
                });
            }
        });
        const config = runtime.getConfig();
        const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
        const host = exports.CraftArchitectEngineWorker.ManagedHost;
        const proofJson = host.GetRuntimeProofJson(runtimeProofChallenge);
        const proof = JSON.parse(proofJson);
        if (proof.protocolVersion !== protocolVersion ||
            proof.challenge !== runtimeProofChallenge ||
            typeof proof.runtimeAssembly !== "string" || proof.runtimeAssembly.length === 0 ||
            typeof proof.challengeHash !== "string" || !/^[0-9a-f]{64}$/i.test(proof.challengeHash) ||
            typeof proof.proofHash !== "string" || !/^[0-9a-f]{64}$/i.test(proof.proofHash)) {
            throw new Error("The managed worker runtime returned an invalid self-check proof.");
        }
        return { ready: true, runtime, host, proof };
    } catch (error) {
        console.error("Managed engine worker startup failed.", error);
        return { ready: false, error: String(error) };
    }
}

function dispatchManagedExecution(message, host) {
    host.ExecuteMessageJson(JSON.stringify(message))
        .then(resultJson => self.postMessage(JSON.parse(resultJson)))
        .catch(error => postProtocolError(message, "managed-execution-rejected", String(error)));
}

function dispatchManagedExecutionJson(messageJson, identity, host) {
    host.ExecuteMessageJson(messageJson)
        .then(resultJson => self.postMessage({
            kind: "managed-json",
            messageJson: resultJson,
            messageKind: computationResultKind
        }))
        .catch(error => postProtocolError(identity, "managed-execution-rejected", String(error)));
}

function postProtocolError(message, code, errorMessage) {
    self.postMessage({
        protocolVersion,
        kind: "protocol-error",
        generation: message.generation,
        executionId: message.executionId ?? null,
        transactionId: message.transactionId ?? null,
        payload: { code, message: errorMessage }
    });
}
