const protocolVersion = "4";
const computationResultKind = "computation-result";
const runtimeProofChallenge = "craft-architect-engine-worker-v1";
const workerInstanceId = crypto.randomUUID();
const workerModuleUrl = new URL(import.meta.url);
const acceptanceMode = workerModuleUrl.searchParams.get("acceptance") === "true";
const workspaceId = normalizeWorkspaceId(
    workerModuleUrl.searchParams.get("workspace") ?? "active");
let workerGeneration = null;
let sessionBootstrapPromise = null;

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
            (message.messageKind !== "execute" &&
             message.messageKind !== "cancel" &&
             message.messageKind !== "session-command")) {
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
        if (message.messageKind === "session-command") {
            dispatchManagedSessionCommandJson(message.messageJson, identity, managedRuntime.host);
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

function dispatchManagedSessionCommandJson(messageJson, identity, host) {
    executeSessionCommand(messageJson, host)
        .then(resultJson => self.postMessage({
            kind: "managed-json",
            messageJson: resultJson,
            messageKind: "session-result"
        }))
        .catch(error => postProtocolError(identity, "session-command-rejected", String(error)));
}

const legacySessionDatabaseName = "FFXIVCraftArchitect";
const legacySessionDatabaseMaximumVersion = 15;
const sessionDatabaseName = "FFXIVCraftArchitect.Engine";
const sessionDatabaseVersion = 1;
const sessionStorageMetadataStore = "storageMetadata";
const legacySessionMigrationId = "legacy-monolith-v15";
const sessionManifestStore = "engineSessionManifests";
const sessionRevisionStore = "engineSessionRevisions";
const sessionComponentStore = "engineSessionComponents";
const legacyPlanStore = "plans";
const savedPlanComponentStore = "planComponents";
const activeSessionManifestId = workspaceId === "active"
    ? "active"
    : `workspace:${workspaceId}:active`;
const sessionRevisionSchemaVersion = 2;
const sessionComponentFields = Object.freeze([
    "revisionMarkerJson",
    "planJson",
    "planStateJson",
    "marketPlansJson",
    "marketIntelligenceJson",
    "marketItemAnalysesJson",
    "marketAnalysisRecipeBasisJson",
    "marketAnalysisScopeSnapshotJson",
    "procurementRouteJson"
]);

async function executeSessionCommand(messageJson, host) {
    const message = JSON.parse(messageJson);
    const command = message.payload ?? {};
    if (command.commandKind === "bootstrap") {
        const bootstrapResultJson = await ensureSessionBootstrapped(host, message);
        const bootstrapResult = JSON.parse(bootstrapResultJson);
        return JSON.stringify({
            ...bootstrapResult,
            generation: message.generation,
            executionId: message.executionId,
            transactionId: message.transactionId
        });
    }

    await ensureSessionBootstrapped(host, message);
    if (command.commandKind === "replace") {
        return await replaceDurableSession(host, message);
    }
    if (typeof command.commandKind === "string" &&
        command.commandKind.startsWith("mutate-")) {
        return await mutateDurableSession(host, message);
    }
    return await host.ExecuteSessionCommandJson(messageJson);
}

async function ensureSessionBootstrapped(host, requestMessage) {
    sessionBootstrapPromise ??= bootstrapSession(host, requestMessage);
    try {
        return await sessionBootstrapPromise;
    } catch (error) {
        sessionBootstrapPromise = null;
        throw error;
    }
}

async function bootstrapSession(host, requestMessage) {
    const startedAt = performance.now();
    const durable = await loadDurableSession();
    const loadedAt = performance.now();
    const restoreMessage = createManagedSessionMessage(
        requestMessage,
        "restore",
        0,
        {
            revision: durable.revision,
            storedPlan: durable.storedPlan,
            trackStoredPlanIdentity: durable.trackStoredPlanIdentity,
            migratedFromLegacy: durable.migratedFromLegacy
        });
    const resultJson = await host.ExecuteSessionCommandJson(JSON.stringify(restoreMessage));
    const restoredAt = performance.now();
    const result = JSON.parse(resultJson);
    if (result.payload?.accepted !== true) {
        return resultJson;
    }

    const repairPatch = result.payload?.durableRepairPatch ?? null;
    if (repairPatch && durable.storedPlan) {
        const repairedState = applyDurablePatch(durable.storedPlan, repairPatch);
        await commitDurableSession(
            durable.migratedFromLegacy ? 0 : durable.revision,
            result.payload.revision,
            repairedState,
            repairPatch,
            durable.trackStoredPlanIdentity);
    } else if (durable.migratedFromLegacy && durable.storedPlan) {
        await commitDurableSession(
            0,
            durable.revision,
            durable.storedPlan,
            null,
            durable.trackStoredPlanIdentity);
    }
    console.info(
        `[EngineSession] bootstrap load=${Math.round(loadedAt - startedAt)}ms ` +
        `managed-restore=${Math.round(restoredAt - loadedAt)}ms ` +
        `total=${Math.round(performance.now() - startedAt)}ms`);
    return resultJson;
}

async function replaceDurableSession(host, requestMessage) {
    const command = requestMessage.payload;
    const current = await loadDurableSession();
    if (current.revision !== command.expectedRevision) {
        return await host.ExecuteSessionCommandJson(JSON.stringify(createManagedSessionMessage(
            requestMessage,
            "shell",
            command.expectedRevision,
            {})));
    }

    const targetRevision = current.revision + 1;
    const restoreMessage = createManagedSessionMessage(
        requestMessage,
        "restore",
        current.revision,
        {
            revision: targetRevision,
            storedPlan: command.payload?.storedPlan ?? null,
            trackStoredPlanIdentity: command.payload?.trackStoredPlanIdentity !== false,
            migratedFromLegacy: false
        });
    const resultJson = await host.ExecuteSessionCommandJson(JSON.stringify(restoreMessage));
    const result = JSON.parse(resultJson);
    if (result.payload?.accepted !== true) {
        return resultJson;
    }

    try {
        const repairPatch = result.payload?.durableRepairPatch ?? null;
        const durableState = repairPatch
            ? applyDurablePatch(command.payload?.storedPlan ?? null, repairPatch)
            : command.payload?.storedPlan ?? null;
        await commitDurableSession(
            current.revision,
            result.payload.revision,
            durableState,
            repairPatch,
            command.payload?.trackStoredPlanIdentity !== false);
        return resultJson;
    } catch (error) {
        sessionBootstrapPromise = null;
        throw new Error(`Worker session durable commit failed: ${String(error)}`);
    }
}

async function mutateDurableSession(host, requestMessage) {
    const startedAt = performance.now();
    const command = requestMessage.payload;
    const current = await loadDurableSession();
    const loadedAt = performance.now();
    if (current.revision !== command.expectedRevision) {
        return await host.ExecuteSessionCommandJson(JSON.stringify(createManagedSessionMessage(
            requestMessage,
            "shell",
            command.expectedRevision,
            {})));
    }

    const resultJson = await host.ExecuteSessionCommandJson(JSON.stringify(requestMessage));
    const managedAt = performance.now();
    const result = JSON.parse(resultJson);
    if (result.payload?.accepted !== true) {
        return resultJson;
    }

    const carrier = result.payload.projection;
    const targetRevision = current.revision + 1;
    if (result.payload.revision !== targetRevision ||
        carrier?.shell?.revision !== targetRevision ||
        (!carrier?.durableState && !carrier?.durablePatch)) {
        throw new Error("Worker mutation did not return one durable successor revision.");
    }

    try {
        const durableState = carrier.durableState ??
            applyDurablePatch(current.storedPlan, carrier.durablePatch);
        await commitDurableSession(
            current.revision,
            targetRevision,
            durableState,
            carrier.durablePatch ?? null,
            current.trackStoredPlanIdentity);
        const committedAt = performance.now();
        console.info(
            `[EngineSession] ${command.commandKind} load=${Math.round(loadedAt - startedAt)}ms ` +
            `managed=${Math.round(managedAt - loadedAt)}ms ` +
            `commit=${Math.round(committedAt - managedAt)}ms ` +
            `durable=${carrier.durablePatch ? "patch" : "snapshot"} ` +
            `total=${Math.round(committedAt - startedAt)}ms`);
        result.payload.projection = {
            shell: carrier.shell,
            view: carrier.publicProjection
        };
        return JSON.stringify(result);
    } catch (error) {
        sessionBootstrapPromise = null;
        throw new Error(`Worker session durable mutation failed: ${String(error)}`);
    }
}

function applyDurablePatch(storedPlan, patch) {
    const hasReplacement =
        patch?.replacePlanJson === true ||
        patch?.replacePlanStateJson === true ||
        patch?.replaceProjectItems === true ||
        patch?.replaceMarketEvidence === true ||
        patch?.replaceProcurementRoute === true ||
        patch?.replaceContext === true ||
        patch?.replaceSourceIdentity === true ||
        Number.isSafeInteger(patch?.procurementTravelTolerance);
    if (!patch || !hasReplacement) {
        throw new Error("Worker session durable patch is incomplete.");
    }
    const successor = {
        ...(storedPlan ?? {
            id: "autosave",
            name: "Autosave",
            projectItems: []
        }),
        modifiedAt: new Date().toISOString(),
        savedAt: storedPlan?.savedAt ?? new Date().toISOString()
    };
    if (patch.replacePlanJson === true) {
        successor.planJson = patch.planJson ?? null;
    }
    if (patch.replacePlanStateJson === true) {
        successor.planStateJson = patch.planStateJson ?? null;
    }
    if (patch.replaceProjectItems === true) {
        successor.projectItems = Array.isArray(patch.projectItems)
            ? patch.projectItems
            : [];
    }
    if (patch.replaceMarketEvidence === true) {
        successor.marketPlansJson = null;
        successor.marketItemAnalysesJson = null;
        successor.marketIntelligenceJson = patch.marketIntelligenceJson ?? null;
        successor.marketAnalysisRecipeBasisJson =
            patch.marketAnalysisRecipeBasisJson ?? null;
        successor.savedRecommendationMode =
            patch.savedRecommendationMode ?? successor.savedRecommendationMode ?? 0;
        successor.savedMarketAnalysisLens =
            patch.savedMarketAnalysisLens ?? successor.savedMarketAnalysisLens ?? 0;
    }
    if (patch.replaceProcurementRoute === true) {
        successor.procurementRouteJson = patch.procurementRouteJson ?? null;
    }
    if (Number.isSafeInteger(patch.procurementTravelTolerance)) {
        successor.procurementTravelTolerance =
            Math.max(0, Math.min(11, patch.procurementTravelTolerance));
    }
    if (patch.replaceContext === true) {
        successor.dataCenter = patch.dataCenter || "Aether";
    }
    if (patch.replaceSourceIdentity === true) {
        successor.sourcePlanId = patch.sourcePlanId ?? null;
        successor.sourcePlanName = patch.sourcePlanName ?? null;
    }
    return successor;
}

function createManagedSessionMessage(requestMessage, commandKind, expectedRevision, payload) {
    return {
        ...requestMessage,
        payload: {
            contractVersion: "1",
            commandKind,
            expectedRevision,
            payload,
            operationId: requestMessage.payload?.operationId ?? null
        }
    };
}

async function loadDurableSession() {
    const database = await openSessionDatabase();
    try {
        const manifest = await readStoreValue(database, sessionManifestStore, activeSessionManifestId);
        if (manifest?.activeRevision > 0) {
            let active = null;
            let activeError = null;
            try {
                active = await loadRevision(database, manifest.activeRevision);
            } catch (error) {
                activeError = error;
            }
            if (active) {
                return {
                    revision: manifest.activeRevision,
                    storedPlan: active.storedPlan,
                    trackStoredPlanIdentity: active.trackStoredPlanIdentity !== false,
                    migratedFromLegacy: false
                };
            }

            if (manifest.previousRevision > 0) {
                const previous = await loadRevision(database, manifest.previousRevision);
                if (previous) {
                    await repairManifestToPrevious(database, manifest.previousRevision);
                    return {
                        revision: manifest.previousRevision,
                        storedPlan: previous.storedPlan,
                        trackStoredPlanIdentity: previous.trackStoredPlanIdentity !== false,
                        migratedFromLegacy: false
                    };
                }
            }
            throw new Error(
                activeError
                    ? `The active Worker session revision is corrupt and no predecessor is recoverable: ${String(activeError)}`
                    : "The Worker session manifest does not reference a recoverable revision.");
        }

        const legacyRecord = workspaceId === "active"
            ? await readStoreValue(database, legacyPlanStore, "autosave")
            : null;
        if (legacyRecord) {
            const legacy = await materializeSavedPlan(database, legacyRecord);
            return {
                revision: 1,
                storedPlan: legacy,
                trackStoredPlanIdentity: false,
                migratedFromLegacy: true
            };
        }
        return {
            revision: 0,
            storedPlan: null,
            trackStoredPlanIdentity: false,
            migratedFromLegacy: false
        };
    } finally {
        database.close();
    }
}

function openSessionDatabase() {
    return new Promise((resolve, reject) => {
        let blocked = false;
        const request = indexedDB.open(sessionDatabaseName, sessionDatabaseVersion);
        request.onerror = () => {
            if (request.error?.name === "VersionError") {
                reject(new Error(
                    `Worker session database uses an incompatible schema newer than v${sessionDatabaseVersion}.`));
                return;
            }
            reject(request.error);
        };
        request.onblocked = () => {
            blocked = true;
            reject(new Error(
                "Worker session database upgrade is blocked by another Craft Architect tab."));
        };
        request.onupgradeneeded = () => {
            const database = request.result;
            if (!database.objectStoreNames.contains(sessionStorageMetadataStore)) {
                database.createObjectStore(sessionStorageMetadataStore, { keyPath: "id" });
            }
            if (!database.objectStoreNames.contains(sessionManifestStore)) {
                database.createObjectStore(sessionManifestStore, { keyPath: "id" });
            }
            if (!database.objectStoreNames.contains(sessionRevisionStore)) {
                const store = database.createObjectStore(sessionRevisionStore, { keyPath: "id" });
                store.createIndex(
                    "createdAtUnixMilliseconds",
                    "createdAtUnixMilliseconds",
                    { unique: false });
            }
            if (!database.objectStoreNames.contains(sessionComponentStore)) {
                const store = database.createObjectStore(sessionComponentStore, { keyPath: "id" });
                store.createIndex(
                    "createdAtUnixMilliseconds",
                    "createdAtUnixMilliseconds",
                    { unique: false });
            }
            if (!database.objectStoreNames.contains(legacyPlanStore)) {
                database.createObjectStore(legacyPlanStore, { keyPath: "id" });
            }
            if (!database.objectStoreNames.contains(savedPlanComponentStore)) {
                const store = database.createObjectStore(savedPlanComponentStore, { keyPath: "id" });
                store.createIndex("planId", "planId", { unique: false });
            }
        };
        request.onsuccess = async () => {
            const database = request.result;
            if (blocked) {
                database.close();
                return;
            }
            const incompatibility = sessionSchemaIncompatibility(database);
            if (incompatibility) {
                database.close();
                reject(new Error(
                    `Worker session database v${sessionDatabaseVersion} has an incompatible schema: ` +
                    `${incompatibility}.`));
                return;
            }
            database.onversionchange = () => database.close();
            try {
                await migrateLegacySessionDatabase(database);
                resolve(database);
            } catch (error) {
                database.close();
                reject(error);
            }
        };
    });
}

function sessionSchemaIncompatibility(database) {
    const requiredStores = [
        sessionStorageMetadataStore,
        sessionManifestStore,
        sessionRevisionStore,
        sessionComponentStore,
        legacyPlanStore,
        savedPlanComponentStore
    ];
    const missingStores = requiredStores.filter(
        storeName => !database.objectStoreNames.contains(storeName));
    if (missingStores.length > 0) {
        return `missing stores ${missingStores.join(", ")}`;
    }

    const transaction = database.transaction(
        [sessionRevisionStore, sessionComponentStore, savedPlanComponentStore],
        "readonly");
    const missingIndexes = [];
    for (const [storeName, indexName] of [
        [sessionRevisionStore, "createdAtUnixMilliseconds"],
        [sessionComponentStore, "createdAtUnixMilliseconds"],
        [savedPlanComponentStore, "planId"]
    ]) {
        if (!transaction.objectStore(storeName).indexNames.contains(indexName)) {
            missingIndexes.push(`${storeName}.${indexName}`);
        }
    }
    return missingIndexes.length > 0
        ? `missing indexes ${missingIndexes.join(", ")}`
        : null;
}

async function migrateLegacySessionDatabase(database) {
    const migration = await readStoreValue(
        database,
        sessionStorageMetadataStore,
        legacySessionMigrationId);
    if (migration?.state === "complete") {
        return;
    }
    if (typeof indexedDB.databases !== "function") {
        throw new Error(
            "This browser cannot enumerate databases, so Worker session migration cannot run safely.");
    }

    const databases = await indexedDB.databases();
    const legacyInfo = databases.find(item => item.name === legacySessionDatabaseName);
    const sourceStores = [
        sessionManifestStore,
        sessionRevisionStore,
        sessionComponentStore,
        legacyPlanStore,
        savedPlanComponentStore
    ];
    const snapshot = Object.fromEntries(sourceStores.map(name => [name, []]));
    if (legacyInfo) {
        if ((legacyInfo.version ?? 0) > legacySessionDatabaseMaximumVersion) {
            throw new Error(
                `Legacy Worker session schema v${legacyInfo.version} is newer than the supported migration reader.`);
        }
        const legacy = await new Promise((resolve, reject) => {
            const request = indexedDB.open(legacySessionDatabaseName);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
        try {
            const available = sourceStores.filter(name => legacy.objectStoreNames.contains(name));
            if (available.length > 0) {
                await new Promise((resolve, reject) => {
                    const transaction = legacy.transaction(available, "readonly");
                    for (const storeName of available) {
                        const request = transaction.objectStore(storeName).getAll();
                        request.onsuccess = () => {
                            snapshot[storeName] = request.result || [];
                        };
                        request.onerror = () => transaction.abort();
                    }
                    transaction.oncomplete = resolve;
                    transaction.onerror = () => reject(transaction.error);
                    transaction.onabort = () => reject(
                        transaction.error ?? new Error("Legacy Worker session snapshot aborted."));
                });
            }
        } finally {
            legacy.close();
        }
    }

    await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [sessionStorageMetadataStore, ...sourceStores],
            "readwrite");
        for (const storeName of sourceStores) {
            const store = transaction.objectStore(storeName);
            for (const record of snapshot[storeName]) {
                store.put(record);
            }
        }
        transaction.objectStore(sessionStorageMetadataStore).put({
            id: legacySessionMigrationId,
            schemaVersion: 1,
            domain: "engine",
            state: "complete",
            sourceDatabase: legacySessionDatabaseName,
            sourceSchemaVersion: legacyInfo?.version ?? null,
            counts: Object.fromEntries(sourceStores.map(name => [name, snapshot[name].length])),
            completedAtUtc: new Date().toISOString()
        });
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error("Worker session migration transaction aborted."));
    });
}

async function materializeSavedPlan(database, record) {
    if (record?.schemaVersion !== 2 ||
        !record.storedPlanMetadata ||
        !record.componentRefs) {
        return record;
    }
    if (!database.objectStoreNames.contains(savedPlanComponentStore)) {
        throw new Error("The autosave references unavailable saved-plan components.");
    }

    const referencedIds = Object.values(record.componentRefs)
        .filter(id => typeof id === "string");
    const components = await readStoreValues(
        database,
        savedPlanComponentStore,
        [...new Set(referencedIds)]);
    const storedPlan = { ...record.storedPlanMetadata };
    for (const field of sessionComponentFields) {
        const componentId = record.componentRefs[field];
        if (componentId == null) {
            storedPlan[field] = null;
            continue;
        }
        const component = components.get(componentId);
        if (!component || component.planId !== record.id || component.field !== field) {
            throw new Error(`The autosave is missing component '${field}'.`);
        }
        storedPlan[field] = component.payload;
    }
    return storedPlan;
}

function readStoreValue(database, storeName, key) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, "readonly");
        const request = transaction.objectStore(storeName).get(key);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result ?? null);
    });
}

function readStoreValues(database, storeName, keys) {
    return new Promise((resolve, reject) => {
        const values = new Map();
        if (keys.length === 0) {
            resolve(values);
            return;
        }
        const transaction = database.transaction(storeName, "readonly");
        const store = transaction.objectStore(storeName);
        for (const key of keys) {
            const request = store.get(key);
            request.onsuccess = () => values.set(key, request.result ?? null);
        }
        transaction.oncomplete = () => resolve(values);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () =>
            reject(transaction.error ?? new Error("Worker session component read aborted."));
    });
}

async function loadRevision(database, revision) {
    const record = await readStoreValue(
        database,
        sessionRevisionStore,
        revisionRecordId(revision));
    if (!record) {
        return null;
    }
    if (record.schemaVersion === sessionRevisionSchemaVersion) {
        return {
            storedPlan: await materializeStoredPlan(database, record),
            trackStoredPlanIdentity: record.trackStoredPlanIdentity !== false
        };
    }
    if (Object.prototype.hasOwnProperty.call(record, "storedPlan")) {
        return {
            storedPlan: record.storedPlan ?? null,
            trackStoredPlanIdentity: record.trackStoredPlanIdentity !== false
        };
    }
    throw new Error(`Worker session revision ${revision} has an unsupported record shape.`);
}

async function materializeStoredPlan(database, record) {
    if (record.storedPlanMetadata === null) {
        return null;
    }
    if (!record.storedPlanMetadata || !record.componentRefs) {
        throw new Error(`Worker session revision ${record.revision} is missing v2 metadata.`);
    }

    const referencedIds = sessionComponentFields
        .map(field => record.componentRefs[field])
        .filter(id => typeof id === "string");
    const components = await readStoreValues(
        database,
        sessionComponentStore,
        [...new Set(referencedIds)]);
    const storedPlan = { ...record.storedPlanMetadata };
    for (const field of sessionComponentFields) {
        const componentId = record.componentRefs[field];
        if (componentId == null) {
            storedPlan[field] = null;
            continue;
        }
        const component = components.get(componentId);
        if (!component || component.field !== field) {
            throw new Error(
                `Worker session revision ${record.revision} is missing component '${field}'.`);
        }
        storedPlan[field] = component.payload;
    }
    return storedPlan;
}

function createV2RevisionRecord(
    revision,
    storedPlan,
    trackStoredPlanIdentity,
    previousRecord,
    durablePatch)
{
    const createdAtUnixMilliseconds = Date.now();
    if (!storedPlan) {
        return {
            record: {
                id: revisionRecordId(revision),
                schemaVersion: sessionRevisionSchemaVersion,
                revision,
                storedPlanMetadata: null,
                componentRefs: Object.fromEntries(
                    sessionComponentFields.map(field => [field, null])),
                trackStoredPlanIdentity,
                createdAtUnixMilliseconds
            },
            components: []
        };
    }

    const metadata = { ...storedPlan };
    for (const field of sessionComponentFields) {
        delete metadata[field];
    }
    const canReuseComponents = Boolean(
        previousRecord?.schemaVersion === sessionRevisionSchemaVersion &&
        durablePatch);
    const changedFields = new Set();
    changedFields.add("revisionMarkerJson");
    if (durablePatch?.replacePlanJson === true) {
        changedFields.add("planJson");
    }
    if (durablePatch?.replacePlanStateJson === true) {
        changedFields.add("planStateJson");
    }
    if (durablePatch?.replaceMarketEvidence === true) {
        changedFields.add("marketPlansJson");
        changedFields.add("marketItemAnalysesJson");
        changedFields.add("marketIntelligenceJson");
        changedFields.add("marketAnalysisRecipeBasisJson");
    }
    if (durablePatch?.replaceProcurementRoute === true) {
        changedFields.add("procurementRouteJson");
    }
    const componentRefs = {};
    const components = [];
    for (const field of sessionComponentFields) {
        const payload = field === "revisionMarkerJson"
            ? JSON.stringify({ schemaVersion: 1, revision })
            : storedPlan[field] ?? null;
        const previousRef = previousRecord?.componentRefs?.[field] ?? null;
        if (canReuseComponents && !changedFields.has(field)) {
            componentRefs[field] = previousRef;
            continue;
        }
        if (payload === null) {
            componentRefs[field] = null;
            continue;
        }
        const id = componentRecordId(revision, field);
        componentRefs[field] = id;
        components.push({
            id,
            schemaVersion: 1,
            field,
            payload,
            createdAtUnixMilliseconds
        });
    }

    return {
        record: {
            id: revisionRecordId(revision),
            schemaVersion: sessionRevisionSchemaVersion,
            revision,
            storedPlanMetadata: metadata,
            componentRefs,
            trackStoredPlanIdentity,
            createdAtUnixMilliseconds
        },
        components
    };
}

async function commitDurableSession(
    expectedRevision,
    revision,
    storedPlan,
    durablePatch = null,
    trackStoredPlanIdentity = storedPlan?.id !== "autosave")
{
    const database = await openSessionDatabase();
    const previousRecord = expectedRevision > 0
        ? await readStoreValue(
            database,
            sessionRevisionStore,
            revisionRecordId(expectedRevision))
        : null;
    const successor = createV2RevisionRecord(
        revision,
        storedPlan,
        trackStoredPlanIdentity,
        previousRecord,
        durablePatch);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [sessionManifestStore, sessionRevisionStore, sessionComponentStore],
            "readwrite");
        const manifests = transaction.objectStore(sessionManifestStore);
        const revisions = transaction.objectStore(sessionRevisionStore);
        const components = transaction.objectStore(sessionComponentStore);
        const manifestRequest = manifests.get(activeSessionManifestId);
        let rejected = false;

        transaction.oncomplete = () => {
            database.close();
            if (!rejected) resolve();
        };
        transaction.onerror = () => {
            database.close();
            reject(transaction.error);
        };
        transaction.onabort = () => {
            database.close();
            if (!rejected) reject(transaction.error ?? new Error("Worker session commit aborted."));
        };
        manifestRequest.onerror = () => transaction.abort();
        manifestRequest.onsuccess = () => {
            const manifest = manifestRequest.result;
            const activeRevision = manifest?.activeRevision ?? 0;
            if (activeRevision !== expectedRevision) {
                rejected = true;
                transaction.abort();
                database.close();
                reject(new Error(
                    `Worker session revision changed from ${expectedRevision} to ${activeRevision}.`));
                return;
            }

            const persistSuccessor = () => {
                for (const component of successor.components) {
                    components.put(component);
                }
                revisions.put(successor.record);
                manifests.put({
                    id: activeSessionManifestId,
                    schemaVersion: sessionRevisionSchemaVersion,
                    activeRevision: revision,
                    previousRevision: activeRevision,
                    updatedAtUnixMilliseconds: Date.now()
                });
            };
            const retiredRevision = manifest?.previousRevision ?? 0;
            if (retiredRevision <= 0 || retiredRevision === activeRevision) {
                persistSuccessor();
                return;
            }

            const retiredRequest = revisions.get(revisionRecordId(retiredRevision));
            retiredRequest.onerror = () => transaction.abort();
            retiredRequest.onsuccess = () => {
                const retainedComponentIds = new Set([
                    ...Object.values(previousRecord?.componentRefs ?? {}),
                    ...Object.values(successor.record.componentRefs ?? {})
                ].filter(id => typeof id === "string"));
                const retiredRecord = retiredRequest.result;
                for (const componentId of Object.values(retiredRecord?.componentRefs ?? {})) {
                    if (typeof componentId === "string" &&
                        !retainedComponentIds.has(componentId)) {
                        components.delete(componentId);
                    }
                }
                revisions.delete(revisionRecordId(retiredRevision));
                persistSuccessor();
            };
        };
    });
}

function repairManifestToPrevious(database, previousRevision) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(sessionManifestStore, "readwrite");
        transaction.objectStore(sessionManifestStore).put({
            id: activeSessionManifestId,
            schemaVersion: sessionRevisionSchemaVersion,
            activeRevision: previousRevision,
            previousRevision: 0,
            updatedAtUnixMilliseconds: Date.now()
        });
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error ?? new Error("Manifest repair aborted."));
    });
}

function revisionRecordId(revision) {
    return `${activeSessionManifestId}:${revision}`;
}

function componentRecordId(revision, field) {
    return `${activeSessionManifestId}:${revision}:${field}`;
}

function normalizeWorkspaceId(value) {
    const normalized = String(value ?? "").trim().toLowerCase();
    if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(normalized)) {
        throw new Error("The Worker workspace id is invalid.");
    }
    return normalized;
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
