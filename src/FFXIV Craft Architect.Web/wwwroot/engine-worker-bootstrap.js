const tenancyProtocolVersion = "1";
const defaultWorkspaceId = "active";
const leaderHeartbeatMilliseconds = 750;
const leaderStaleMilliseconds = 3000;
const relayRetryMilliseconds = 500;
const relayAcknowledgementTimeoutMilliseconds = 2000;
const completedRequestRetentionMilliseconds = 30000;
const maximumRememberedRequests = 128;

export function createEngineWorker(workerUrl = "engine-worker.js", workspaceId = defaultWorkspaceId) {
    const normalizedWorkspaceId = normalizeWorkspaceId(workspaceId);
    const resolvedWorkerUrl = new URL(workerUrl, document.baseURI);
    resolvedWorkerUrl.searchParams.set("workspace", normalizedWorkspaceId);
    const worker = new Worker(resolvedWorkerUrl, {
        type: "module",
        name: `craft-architect-engine-${normalizedWorkspaceId}`
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

export function createEngineWorkerController(
    callback,
    workerUrl = "engine-worker.js",
    workspaceId = defaultWorkspaceId,
    requestFreshAuthority = false)
{
    if (!callback || typeof callback.invokeMethodAsync !== "function") {
        throw new TypeError("A .NET Worker callback is required.");
    }
    if (!globalThis.navigator?.locks ||
        typeof globalThis.BroadcastChannel !== "function" ||
        typeof globalThis.crypto?.randomUUID !== "function") {
        throw new Error(
            "Multi-tab Worker authority requires Web Locks, BroadcastChannel, and crypto.randomUUID.");
    }

    const normalizedWorkspaceId = normalizeWorkspaceId(workspaceId);
    const clientId = crypto.randomUUID();
    const lockName = `craft-architect-engine:${normalizedWorkspaceId}`;
    const channelName = `${lockName}:coordination-v${tenancyProtocolVersion}`;
    const channel = new BroadcastChannel(channelName);
    const pendingPingGenerations = new Set();
    const pendingRemotePings = new Map();
    const pendingRelays = new Map();
    const requestOwners = new Map();
    const rememberedRequests = new Map();
    let disposed = false;
    let leaseRequestActive = false;
    let releaseLease = null;
    let localLeaderEpoch = null;
    let observedLeaderEpoch = null;
    let observedLeaderAt = 0;
    let physicalWorkerGeneration = 0;
    let physicalWorkerCapability = null;
    let workerController = null;
    let unsubscribeWorker = null;
    let reportWorkerError = null;
    let heartbeatTimer = null;
    let retryTimer = null;

    channel.addEventListener("message", receiveCoordinationMessage);
    window.addEventListener("pagehide", disposeForPageExit);
    window.addEventListener("pageshow", resumeFromPageCache);
    retryTimer = window.setInterval(runFollowerMaintenance, relayRetryMilliseconds);
    if (requestFreshAuthority === true) {
        postCoordination({ type: "authority-restart-request" });
    }
    requestLease();

    function postCoordination(message) {
        if (disposed) return;
        channel.postMessage({
            tenancyProtocolVersion,
            workspaceId: normalizedWorkspaceId,
            fromClientId: clientId,
            ...message
        });
    }

    function receiveCoordinationMessage(event) {
        if (disposed) return;
        const message = event.data;
        if (message?.tenancyProtocolVersion !== tenancyProtocolVersion ||
            message.workspaceId !== normalizedWorkspaceId ||
            message.fromClientId === clientId) {
            return;
        }

        switch (message.type) {
            case "leader-heartbeat":
                observeLeader(message.leaderEpoch);
                break;
            case "ping-request":
                if (isLeader()) {
                    answerPing(message.fromClientId, message.generation);
                }
                break;
            case "command-request":
                if (isLeader()) {
                    acceptRelayedCommand(message);
                }
                break;
            case "authority-restart-request":
                if (isLeader()) {
                    retirePhysicalAuthority();
                }
                break;
            case "command-accepted":
                if (message.targetClientId === clientId) {
                    acknowledgeRelayedCommand(message);
                }
                break;
            case "worker-message":
                if (message.targetClientId === clientId) {
                    receiveTargetedWorkerMessage(message);
                }
                break;
            case "session-projection":
                if (message.originClientId !== clientId) {
                    dispatchToDotNet(message.message);
                }
                break;
            case "worker-error":
                if (message.targetClientId === clientId || message.targetClientId === "*") {
                    callback.invokeMethodAsync(
                        "ReceiveError",
                        message.errorKind,
                        message.errorMessage).catch(() => {});
                }
                break;
        }
    }

    function observeLeader(leaderEpoch) {
        if (typeof leaderEpoch !== "string" || leaderEpoch.length === 0) {
            return;
        }
        const changed = observedLeaderEpoch !== leaderEpoch;
        observedLeaderEpoch = leaderEpoch;
        observedLeaderAt = Date.now();
        if (!changed) return;

        for (const relay of pendingRelays.values()) {
            relay.acceptedEpoch = null;
            relay.lastSentAt = 0;
        }
        resendPendingPings();
        resendPendingRelays();
    }

    function runFollowerMaintenance() {
        if (disposed) return;
        forgetExpiredRequests();
        if (isLeader()) return;

        if (observedLeaderAt > 0 &&
            Date.now() - observedLeaderAt > leaderStaleMilliseconds) {
            observedLeaderEpoch = null;
            observedLeaderAt = 0;
            for (const relay of pendingRelays.values()) {
                relay.acceptedEpoch = null;
            }
        }
        requestLease();
        resendPendingPings();
        resendPendingRelays();
    }

    function requestLease() {
        if (disposed || leaseRequestActive || isLeader()) return;
        leaseRequestActive = true;
        navigator.locks.request(
            lockName,
            { mode: "exclusive", ifAvailable: true },
            async lock => {
                if (!lock || disposed) return;
                await holdLeaderLease();
            })
            .catch(error => reportCoordinationError("lease-error", error))
            .finally(() => {
                leaseRequestActive = false;
            });
    }

    async function holdLeaderLease() {
        localLeaderEpoch = crypto.randomUUID();
        observedLeaderEpoch = localLeaderEpoch;
        observedLeaderAt = Date.now();
        physicalWorkerGeneration++;
        physicalWorkerCapability = null;

        workerController = createEngineWorker(
            workerUrl,
            normalizedWorkspaceId);
        unsubscribeWorker = workerController.subscribe(routePhysicalWorkerMessage);
        reportWorkerError = event => {
            const message = event?.message ?? "The Worker emitted an unstructured error.";
            callback.invokeMethodAsync("ReceiveError", event.type, message).catch(() => {});
            postCoordination({
                type: "worker-error",
                targetClientId: "*",
                errorKind: event.type,
                errorMessage: message
            });
        };
        workerController.worker.addEventListener("error", reportWorkerError);
        workerController.worker.addEventListener("messageerror", reportWorkerError);
        heartbeatTimer = window.setInterval(
            announceLeadership,
            leaderHeartbeatMilliseconds);
        announceLeadership();
        workerController.ping(physicalWorkerGeneration);

        await new Promise(resolve => {
            releaseLease = resolve;
        });
        releaseLease = null;
        stopPhysicalWorker();
        localLeaderEpoch = null;
    }

    function announceLeadership() {
        if (!isLeader()) return;
        observedLeaderAt = Date.now();
        postCoordination({
            type: "leader-heartbeat",
            leaderEpoch: localLeaderEpoch
        });
    }

    function stopPhysicalWorker() {
        if (heartbeatTimer !== null) {
            window.clearInterval(heartbeatTimer);
            heartbeatTimer = null;
        }
        if (workerController) {
            if (reportWorkerError) {
                workerController.worker.removeEventListener("error", reportWorkerError);
                workerController.worker.removeEventListener("messageerror", reportWorkerError);
            }
            unsubscribeWorker?.();
            workerController.terminate();
        }
        workerController = null;
        unsubscribeWorker = null;
        reportWorkerError = null;
        physicalWorkerCapability = null;
        pendingRemotePings.clear();
        requestOwners.clear();
    }

    function retirePhysicalAuthority() {
        if (!isLeader()) return;
        const release = releaseLease;
        localLeaderEpoch = null;
        stopPhysicalWorker();
        release?.();
    }

    function isLeader() {
        return localLeaderEpoch !== null && workerController !== null;
    }

    function requestPing(generation) {
        if (!Number.isSafeInteger(generation) || generation <= 0) {
            throw new RangeError("A positive worker generation is required.");
        }
        pendingPingGenerations.add(generation);
        if (isLeader()) {
            answerPing(clientId, generation);
        } else {
            postCoordination({ type: "ping-request", generation });
        }
    }

    function resendPendingPings() {
        if (isLeader()) {
            for (const generation of pendingPingGenerations) {
                answerPing(clientId, generation);
            }
            return;
        }
        for (const generation of pendingPingGenerations) {
            postCoordination({ type: "ping-request", generation });
        }
    }

    function answerPing(targetClientId, generation) {
        if (!Number.isSafeInteger(generation) || generation <= 0) return;
        if (!physicalWorkerCapability) {
            let generations = pendingRemotePings.get(targetClientId);
            if (!generations) {
                generations = new Set();
                pendingRemotePings.set(targetClientId, generations);
            }
            generations.add(generation);
            return;
        }

        const capability = {
            ...physicalWorkerCapability,
            generation,
            payload: {
                ...physicalWorkerCapability.payload,
                generation
            }
        };
        deliverToClient(targetClientId, capability);
    }

    function sendJson(messageJson, generation, kind) {
        if (disposed) throw new Error("The Worker controller is disposed.");
        if (typeof messageJson !== "string" || messageJson.length === 0 ||
            !Number.isSafeInteger(generation) || generation <= 0 ||
            (kind !== "execute" && kind !== "cancel" && kind !== "session-command")) {
            throw new TypeError("A valid managed Worker JSON message is required.");
        }
        const identity = JSON.parse(messageJson);
        if (identity?.protocolVersion !== "4" ||
            identity?.generation !== generation ||
            typeof identity?.executionId !== "string" ||
            typeof identity?.transactionId !== "string" ||
            identity?.kind !== kind) {
            throw new TypeError("Managed Worker JSON identity is invalid.");
        }

        const requestId = crypto.randomUUID();
        const relay = {
            requestId,
            messageJson,
            generation,
            kind,
            acceptedEpoch: null,
            lastSentAt: 0,
            expectsTerminal: kind !== "cancel"
        };
        pendingRelays.set(requestId, relay);
        sendRelay(relay);
    }

    function sendRelay(relay) {
        relay.lastSentAt = Date.now();
        const message = {
            type: "command-request",
            requestId: relay.requestId,
            targetLeaderEpoch: observedLeaderEpoch,
            messageJson: relay.messageJson,
            logicalGeneration: relay.generation,
            messageKind: relay.kind
        };
        if (isLeader()) {
            acceptRelayedCommand({
                ...message,
                fromClientId: clientId
            });
        } else {
            postCoordination(message);
        }
    }

    function resendPendingRelays() {
        const now = Date.now();
        for (const relay of pendingRelays.values()) {
            const acceptedByCurrentLeader =
                observedLeaderEpoch !== null &&
                relay.acceptedEpoch === observedLeaderEpoch;
            if (!acceptedByCurrentLeader &&
                now - relay.lastSentAt >= relayAcknowledgementTimeoutMilliseconds) {
                sendRelay(relay);
            }
        }
    }

    function acceptRelayedCommand(message) {
        if (!isLeader() ||
            typeof message.requestId !== "string" ||
            typeof message.messageJson !== "string" ||
            !Number.isSafeInteger(message.logicalGeneration) ||
            !["execute", "cancel", "session-command"].includes(message.messageKind)) {
            return;
        }

        acknowledgeClient(message.fromClientId, message.requestId);
        const remembered = rememberedRequests.get(message.requestId);
        if (remembered) {
            if (remembered.terminalMessage) {
                sendWorkerMessage(
                    message.fromClientId,
                    message.requestId,
                    remembered.terminalMessage);
            }
            return;
        }

        const identity = JSON.parse(message.messageJson);
        const key = commandIdentityKey(identity);
        const existingOwner = requestOwners.get(key);
        if (!existingOwner || message.messageKind !== "cancel") {
            requestOwners.set(key, {
                clientId: message.fromClientId,
                logicalGeneration: message.logicalGeneration,
                requestId: message.requestId
            });
        }
        rememberRequest(message.requestId, {
            acceptedAt: Date.now(),
            terminalMessage: null
        });

        const physicalIdentity = {
            ...identity,
            generation: physicalWorkerGeneration
        };
        workerController.send({
            kind: "managed-json",
            messageJson: JSON.stringify(physicalIdentity),
            generation: physicalWorkerGeneration,
            messageKind: message.messageKind,
            executionId: physicalIdentity.executionId,
            transactionId: physicalIdentity.transactionId
        });
    }

    function acknowledgeClient(targetClientId, requestId) {
        const message = {
            type: "command-accepted",
            targetClientId,
            requestId,
            leaderEpoch: localLeaderEpoch
        };
        if (targetClientId === clientId) {
            acknowledgeRelayedCommand(message);
        } else {
            postCoordination(message);
        }
    }

    function acknowledgeRelayedCommand(message) {
        const relay = pendingRelays.get(message.requestId);
        if (!relay) return;
        relay.acceptedEpoch = message.leaderEpoch;
        if (!relay.expectsTerminal) {
            pendingRelays.delete(message.requestId);
        }
    }

    function routePhysicalWorkerMessage(message) {
        const identity = extractWorkerIdentity(message);
        if (!identity) return;
        if (identity.kind === "capability") {
            physicalWorkerCapability = identity;
            for (const generation of [...pendingPingGenerations]) {
                answerPing(clientId, generation);
            }
            for (const [targetClientId, generations] of pendingRemotePings) {
                for (const generation of generations) {
                    answerPing(targetClientId, generation);
                }
            }
            pendingRemotePings.clear();
            return;
        }

        const owner = requestOwners.get(commandIdentityKey(identity));
        if (!owner) {
            return;
        }
        const logicalMessage = rewriteWorkerGeneration(
            message,
            owner.logicalGeneration);
        const terminal = isTerminalWorkerMessage(logicalMessage);
        if (terminal) {
            const remembered = rememberedRequests.get(owner.requestId);
            if (remembered) {
                remembered.terminalMessage = logicalMessage;
                remembered.completedAt = Date.now();
            }
            requestOwners.delete(commandIdentityKey(identity));
        }
        sendWorkerMessage(owner.clientId, owner.requestId, logicalMessage);
        if (isAcceptedSessionResult(logicalMessage)) {
            broadcastCompactSessionProjection(owner.clientId, logicalMessage);
        }
    }

    function sendWorkerMessage(targetClientId, requestId, message) {
        if (targetClientId === clientId) {
            receiveTargetedWorkerMessage({ requestId, message });
        } else {
            postCoordination({
                type: "worker-message",
                targetClientId,
                requestId,
                message
            });
        }
    }

    function receiveTargetedWorkerMessage(message) {
        const identity = extractWorkerIdentity(message.message);
        if (identity?.kind === "capability") {
            pendingPingGenerations.delete(identity.generation);
        }
        if (isTerminalWorkerMessage(message.message)) {
            pendingRelays.delete(message.requestId);
        }
        dispatchToDotNet(message.message);
    }

    function deliverToClient(targetClientId, message) {
        if (targetClientId === clientId) {
            pendingPingGenerations.delete(message.generation);
            dispatchToDotNet(message);
            return;
        }
        postCoordination({
            type: "worker-message",
            targetClientId,
            requestId: null,
            message
        });
    }

    function broadcastCompactSessionProjection(originClientId, message) {
        const identity = extractWorkerIdentity(message);
        const result = identity?.payload;
        const publishesShell =
            result?.commandKind === "bootstrap" ||
            result?.commandKind === "replace" ||
            result?.commandKind === "shell" ||
            result?.commandKind?.startsWith("operation-") === true ||
            result?.commandKind?.startsWith("mutate-") === true;
        if (!publishesShell) return;
        const shell = result?.projection?.shell ?? result?.projection ?? null;
        if (!shell ||
            typeof shell.hasSession !== "boolean" ||
            !shell.versions ||
            !Number.isSafeInteger(result?.revision)) {
            return;
        }
        const projectionMessage = {
            protocolVersion: "4",
            kind: "cross-tab-session-projection",
            generation: 0,
            executionId: null,
            transactionId: null,
            payload: {
                workspaceId: normalizedWorkspaceId,
                revision: result.revision,
                commandKind: result.commandKind,
                shell
            }
        };
        if (originClientId !== clientId) {
            dispatchToDotNet(projectionMessage);
        }
        postCoordination({
            type: "session-projection",
            originClientId,
            message: projectionMessage
        });
    }

    function dispatchToDotNet(message) {
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
    }

    function rememberRequest(requestId, state) {
        rememberedRequests.set(requestId, state);
        forgetExpiredRequests();
        while (rememberedRequests.size > maximumRememberedRequests) {
            const oldest = rememberedRequests.keys().next().value;
            rememberedRequests.delete(oldest);
        }
    }

    function forgetExpiredRequests() {
        const now = Date.now();
        for (const [requestId, state] of rememberedRequests) {
            if (state.completedAt &&
                now - state.completedAt > completedRequestRetentionMilliseconds) {
                rememberedRequests.delete(requestId);
            }
        }
    }

    function reportCoordinationError(kind, error) {
        if (disposed) return;
        callback.invokeMethodAsync(
            "ReceiveError",
            kind,
            error instanceof Error ? error.message : String(error)).catch(() => {});
    }

    function terminate() {
        if (disposed) return;
        disposed = true;
        if (retryTimer !== null) {
            window.clearInterval(retryTimer);
            retryTimer = null;
        }
        window.removeEventListener("pagehide", disposeForPageExit);
        window.removeEventListener("pageshow", resumeFromPageCache);
        channel.removeEventListener("message", receiveCoordinationMessage);
        channel.close();
        retirePhysicalAuthority();
        pendingPingGenerations.clear();
        pendingRelays.clear();
    }

    function disposeForPageExit(event) {
        if (event?.persisted === true) {
            retirePhysicalAuthority();
            observedLeaderEpoch = null;
            observedLeaderAt = 0;
            return;
        }
        terminate();
    }

    function resumeFromPageCache(event) {
        if (disposed || event?.persisted !== true) return;
        requestLease();
        resendPendingPings();
        resendPendingRelays();
    }

    return {
        ping(generation) {
            if (disposed) throw new Error("The Worker controller is disposed.");
            requestPing(generation);
        },
        sendJson,
        terminate,
        dispose: terminate
    };
}

function normalizeWorkspaceId(workspaceId) {
    const normalized = String(workspaceId ?? "").trim().toLowerCase();
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(normalized)) {
        throw new TypeError(
            "A workspace id must contain 1-64 lowercase letters, numbers, dots, underscores, or hyphens.");
    }
    return normalized;
}

function extractWorkerIdentity(message) {
    if (message?.kind === "managed-json" && typeof message.messageJson === "string") {
        try {
            return JSON.parse(message.messageJson);
        } catch {
            return null;
        }
    }
    return message && typeof message === "object" ? message : null;
}

function rewriteWorkerGeneration(message, logicalGeneration) {
    if (message?.kind === "managed-json" && typeof message.messageJson === "string") {
        const identity = JSON.parse(message.messageJson);
        return {
            ...message,
            messageJson: JSON.stringify({
                ...identity,
                generation: logicalGeneration
            })
        };
    }
    return {
        ...message,
        generation: logicalGeneration
    };
}

function commandIdentityKey(identity) {
    return `${identity?.executionId ?? ""}:${identity?.transactionId ?? ""}`;
}

function isTerminalWorkerMessage(message) {
    const identity = extractWorkerIdentity(message);
    return identity?.kind === "computation-result" ||
        identity?.kind === "session-result" ||
        identity?.kind === "protocol-error";
}

function isAcceptedSessionResult(message) {
    const identity = extractWorkerIdentity(message);
    return identity?.kind === "session-result" &&
        identity.payload?.accepted === true;
}

export function reportEngineHostFinalized() {
    window.dispatchEvent(new Event("craft-architect-engine-host-finalized"));
}

export function yieldToBrowser() {
    return new Promise(resolve => setTimeout(resolve, 0));
}
