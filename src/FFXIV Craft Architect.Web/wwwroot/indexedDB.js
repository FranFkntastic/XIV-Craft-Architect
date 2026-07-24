// IndexedDB module for FFXIV Craft Architect Web
// Uses Unix timestamps (seconds since epoch) for serialization safety

const DB_NAME = 'FFXIVCraftArchitect';
const DB_VERSION = 15;  // Adds component-referenced saved plans
const MODULE_REVISION = 19;
const APPROXIMATE_MARKET_ENTRY_BYTES = 256 * 1024;
const ENGINE_TERMINAL_RETENTION_LIMIT = 128;
const ENGINE_TERMINAL_RETENTION_SCHEMA = 1;
const STORE_PLANS = 'plans';
const STORE_PLAN_COMPONENTS = 'planComponents';
const STORE_PLAN_SUMMARIES = 'planSummaries';
const STORE_SETTINGS = 'settings';
const STORE_MARKET_CACHE = 'marketCache';
const STORE_TRADE_COMPANY_PROFILES = 'tradeCompanyProfiles';
const STORE_TRADE_CRAFTERS = 'tradeCrafters';
const STORE_TRADE_ORDERS = 'tradeOrders';
const STORE_TRADE_ORDER_CRAFT_SNAPSHOTS = 'tradeOrderCraftSnapshots';
const STORE_TRADE_PAYROLL_DRAFTS = 'tradePayrollDrafts';
const STORE_ENGINE_TRANSACTIONS = 'engineTransactions';
const STORE_ENGINE_SESSION_MANIFESTS = 'engineSessionManifests';
const STORE_ENGINE_SESSION_REVISIONS = 'engineSessionRevisions';
const STORE_ENGINE_SESSION_COMPONENTS = 'engineSessionComponents';
const STORED_PLAN_SCHEMA_VERSION = 2;
const STORED_PLAN_COMPONENT_FIELDS = Object.freeze([
    'planJson',
    'marketPlansJson',
    'marketIntelligenceJson',
    'marketItemAnalysesJson',
    'marketAnalysisRecipeBasisJson',
    'marketAnalysisScopeSnapshotJson',
    'procurementRouteJson'
]);

let db = null;
const engineTransactionLocks = new Map();

function attachDatabaseConnection(database, openMessage) {
    db = database;
    db.onversionchange = () => {
        console.warn('[IndexedDB] Database version changed; closing stale connection.');
        db?.close();
        db = null;
    };
    console.log(openMessage);
    return db;
}

function openExistingDatabaseVersion() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME);

        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            const database = attachDatabaseConnection(
                request.result,
                `[IndexedDB] Database opened successfully (existing v${request.result.version}; app requested v${DB_VERSION})`);
            resolve(database);
        };
    });
}

/**
 * Initialize the IndexedDB database
 */
async function initDB() {
    if (db) return db;
    
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        
        request.onerror = () => {
            if (request.error?.name === 'VersionError') {
                console.warn(
                    `[IndexedDB] Existing database is newer than app schema v${DB_VERSION}; opening existing version.`);
                openExistingDatabaseVersion().then(resolve).catch(reject);
                return;
            }

            reject(request.error);
        };
        request.onblocked = () => {
            const message = '[IndexedDB] Database upgrade blocked by another open tab. Close other FFXIV Craft Architect tabs and reload.';
            console.warn(message);
            reject(new Error(message));
        };
        request.onsuccess = () => {
            const database = attachDatabaseConnection(
                request.result,
                `[IndexedDB] Database opened successfully (schema v${request.result.version}; module r${MODULE_REVISION})`);
            ensureTradeStores(database).then(resolve).catch(reject);
        };
        
        request.onupgradeneeded = (event) => {
            const database = event.target.result;
            const oldVersion = event.oldVersion || 0;
            
            // Plans store
            if (!database.objectStoreNames.contains(STORE_PLANS)) {
                const planStore = database.createObjectStore(STORE_PLANS, { keyPath: 'id' });
                planStore.createIndex('name', 'name', { unique: false });
                planStore.createIndex('modifiedAt', 'modifiedAt', { unique: false });
            }

            if (!database.objectStoreNames.contains(STORE_PLAN_COMPONENTS)) {
                const componentStore = database.createObjectStore(
                    STORE_PLAN_COMPONENTS,
                    { keyPath: 'id' });
                componentStore.createIndex('planId', 'planId', { unique: false });
            }

            if (!database.objectStoreNames.contains(STORE_PLAN_SUMMARIES)) {
                const summaryStore = database.createObjectStore(STORE_PLAN_SUMMARIES, { keyPath: 'id' });
                summaryStore.createIndex('name', 'name', { unique: false });
                summaryStore.createIndex('modifiedAt', 'modifiedAt', { unique: false });
                summaryStore.createIndex('savedAt', 'savedAt', { unique: false });
            }
            
            // Settings store
            if (!database.objectStoreNames.contains(STORE_SETTINGS)) {
                database.createObjectStore(STORE_SETTINGS, { keyPath: 'key' });
            }
            
            // Market cache store - migrate to Unix timestamps (v3)
            if (oldVersion < 3 && database.objectStoreNames.contains(STORE_MARKET_CACHE)) {
                database.deleteObjectStore(STORE_MARKET_CACHE);
                console.log('[IndexedDB] Deleted old market cache store for migration');
            }

            if (!database.objectStoreNames.contains(STORE_MARKET_CACHE)) {
                const cacheStore = database.createObjectStore(STORE_MARKET_CACHE, { keyPath: 'key' });
                cacheStore.createIndex('fetchedAtUnix', 'fetchedAtUnix', { unique: false });
                console.log('[IndexedDB] Created market cache store with Unix timestamp index');
            } else {
                const cacheStore = event.target.transaction.objectStore(STORE_MARKET_CACHE);
                if (!cacheStore.indexNames.contains('fetchedAtUnix')) {
                    cacheStore.createIndex('fetchedAtUnix', 'fetchedAtUnix', { unique: false });
                    console.log('[IndexedDB] Repaired missing market cache timestamp index');
                }
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES)) {
                const profileStore = database.createObjectStore(STORE_TRADE_COMPANY_PROFILES, { keyPath: 'id' });
                profileStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Created Trade company profile store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_CRAFTERS)) {
                const crafterStore = database.createObjectStore(STORE_TRADE_CRAFTERS, { keyPath: 'id' });
                crafterStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                crafterStore.createIndex('displayName', 'displayName', { unique: false });
                console.log('[IndexedDB] Created Trade crafter store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_ORDERS)) {
                const orderStore = database.createObjectStore(STORE_TRADE_ORDERS, { keyPath: 'id' });
                orderStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                orderStore.createIndex('status', 'status', { unique: false });
                orderStore.createIndex('commissionedAtUtc', 'commissionedAtUtc', { unique: false });
                console.log('[IndexedDB] Created Trade order store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS)) {
                const snapshotStore = database.createObjectStore(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, { keyPath: 'id' });
                snapshotStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                snapshotStore.createIndex('orderId', 'orderId', { unique: false });
                snapshotStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Created Trade order craft snapshot store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS)) {
                const payrollStore = database.createObjectStore(STORE_TRADE_PAYROLL_DRAFTS, { keyPath: 'id' });
                payrollStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                payrollStore.createIndex('orderId', 'orderId', { unique: false });
                payrollStore.createIndex('planSessionVersion', 'planSessionVersion', { unique: false });
                payrollStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Created Trade payroll draft store');
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_TRANSACTIONS)) {
                const engineStore = database.createObjectStore(STORE_ENGINE_TRANSACTIONS, { keyPath: 'transactionId' });
                engineStore.createIndex('updatedAtUnixMilliseconds', 'updatedAtUnixMilliseconds', { unique: false });
                engineStore.createIndex('terminalUpdatedAtUnixMilliseconds', 'terminalUpdatedAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Created durable engine transaction ledger');
            } else {
                const engineStore = event.target.transaction.objectStore(STORE_ENGINE_TRANSACTIONS);
                if (!engineStore.indexNames.contains('updatedAtUnixMilliseconds')) {
                    engineStore.createIndex('updatedAtUnixMilliseconds', 'updatedAtUnixMilliseconds', { unique: false });
                }
                if (!engineStore.indexNames.contains('terminalUpdatedAtUnixMilliseconds')) {
                    backfillEngineTerminalRetention(engineStore, () =>
                        engineStore.createIndex(
                            'terminalUpdatedAtUnixMilliseconds',
                            'terminalUpdatedAtUnixMilliseconds',
                            { unique: false }));
                }
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_MANIFESTS)) {
                database.createObjectStore(STORE_ENGINE_SESSION_MANIFESTS, { keyPath: 'id' });
                console.log('[IndexedDB] Created Worker session manifest store');
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_REVISIONS)) {
                const revisionStore = database.createObjectStore(
                    STORE_ENGINE_SESSION_REVISIONS,
                    { keyPath: 'id' });
                revisionStore.createIndex('createdAtUnixMilliseconds', 'createdAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Created Worker session revision store');
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_COMPONENTS)) {
                const componentStore = database.createObjectStore(
                    STORE_ENGINE_SESSION_COMPONENTS,
                    { keyPath: 'id' });
                componentStore.createIndex('createdAtUnixMilliseconds', 'createdAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Created Worker session component store');
            }

        };
    });
}

function formatIndexedDbError(error) {
    if (!error) {
        return 'Unknown IndexedDB error.';
    }

    if (error.message) {
        return error.message;
    }

    if (error.name) {
        return error.name;
    }

    return String(error);
}

function createTradeStoreDiagnostics(database, errorMessage = null) {
    return {
        databaseVersion: database?.version || 0,
        hasCompanyProfilesStore: Boolean(database?.objectStoreNames?.contains(STORE_TRADE_COMPANY_PROFILES)),
        hasCraftersStore: Boolean(database?.objectStoreNames?.contains(STORE_TRADE_CRAFTERS)),
        hasOrdersStore: Boolean(database?.objectStoreNames?.contains(STORE_TRADE_ORDERS)),
        hasOrderCraftSnapshotsStore: Boolean(database?.objectStoreNames?.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS)),
        hasPayrollDraftsStore: Boolean(database?.objectStoreNames?.contains(STORE_TRADE_PAYROLL_DRAFTS)),
        errorMessage
    };
}

function hasRequiredTradeStores(database) {
    return database.objectStoreNames.contains(STORE_PLAN_COMPONENTS) &&
        database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES) &&
        database.objectStoreNames.contains(STORE_TRADE_CRAFTERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS) &&
        database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS) &&
        database.objectStoreNames.contains(STORE_ENGINE_TRANSACTIONS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_MANIFESTS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_REVISIONS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_COMPONENTS);
}

async function ensureTradeStores(database) {
    if (hasRequiredTradeStores(database)) {
        return database;
    }

    console.warn(
        `[IndexedDB] Trade stores missing in database v${database.version}; opening a repair upgrade.`);
    if (db === database) {
        db = null;
    }
    database.close();
    return await openTradeStoreRepairUpgrade(database.version + 1);
}

function openTradeStoreRepairUpgrade(repairVersion) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, repairVersion);

        request.onerror = () => reject(request.error);
        request.onblocked = () => {
            const message = '[IndexedDB] Trade store repair blocked by another open tab. Close other FFXIV Craft Architect tabs and reload.';
            console.warn(message);
            reject(new Error(message));
        };
        request.onupgradeneeded = (event) => {
            const database = event.target.result;

            if (!database.objectStoreNames.contains(STORE_PLAN_COMPONENTS)) {
                const componentStore = database.createObjectStore(
                    STORE_PLAN_COMPONENTS,
                    { keyPath: 'id' });
                componentStore.createIndex('planId', 'planId', { unique: false });
                console.log('[IndexedDB] Repaired missing saved-plan component store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES)) {
                const profileStore = database.createObjectStore(STORE_TRADE_COMPANY_PROFILES, { keyPath: 'id' });
                profileStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Repaired missing Trade company profile store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_CRAFTERS)) {
                const crafterStore = database.createObjectStore(STORE_TRADE_CRAFTERS, { keyPath: 'id' });
                crafterStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                crafterStore.createIndex('displayName', 'displayName', { unique: false });
                console.log('[IndexedDB] Repaired missing Trade crafter store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_ORDERS)) {
                const orderStore = database.createObjectStore(STORE_TRADE_ORDERS, { keyPath: 'id' });
                orderStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                orderStore.createIndex('status', 'status', { unique: false });
                orderStore.createIndex('commissionedAtUtc', 'commissionedAtUtc', { unique: false });
                console.log('[IndexedDB] Repaired missing Trade order store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS)) {
                const snapshotStore = database.createObjectStore(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, { keyPath: 'id' });
                snapshotStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                snapshotStore.createIndex('orderId', 'orderId', { unique: false });
                snapshotStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Repaired missing Trade order craft snapshot store');
            }

            if (!database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS)) {
                const payrollStore = database.createObjectStore(STORE_TRADE_PAYROLL_DRAFTS, { keyPath: 'id' });
                payrollStore.createIndex('companyProfileId', 'companyProfileId', { unique: false });
                payrollStore.createIndex('orderId', 'orderId', { unique: false });
                payrollStore.createIndex('planSessionVersion', 'planSessionVersion', { unique: false });
                payrollStore.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
                console.log('[IndexedDB] Repaired missing Trade payroll draft store');
            }


            if (!database.objectStoreNames.contains(STORE_ENGINE_TRANSACTIONS)) {
                const engineStore = database.createObjectStore(STORE_ENGINE_TRANSACTIONS, { keyPath: 'transactionId' });
                engineStore.createIndex('updatedAtUnixMilliseconds', 'updatedAtUnixMilliseconds', { unique: false });
                engineStore.createIndex('terminalUpdatedAtUnixMilliseconds', 'terminalUpdatedAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Repaired missing durable engine transaction ledger');
            } else {
                const engineStore = event.target.transaction.objectStore(STORE_ENGINE_TRANSACTIONS);
                if (!engineStore.indexNames.contains('updatedAtUnixMilliseconds')) {
                    engineStore.createIndex('updatedAtUnixMilliseconds', 'updatedAtUnixMilliseconds', { unique: false });
                }
                if (!engineStore.indexNames.contains('terminalUpdatedAtUnixMilliseconds')) {
                    backfillEngineTerminalRetention(engineStore, () =>
                        engineStore.createIndex(
                            'terminalUpdatedAtUnixMilliseconds',
                            'terminalUpdatedAtUnixMilliseconds',
                            { unique: false }));
                }
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_MANIFESTS)) {
                database.createObjectStore(STORE_ENGINE_SESSION_MANIFESTS, { keyPath: 'id' });
                console.log('[IndexedDB] Repaired missing Worker session manifest store');
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_REVISIONS)) {
                const revisionStore = database.createObjectStore(
                    STORE_ENGINE_SESSION_REVISIONS,
                    { keyPath: 'id' });
                revisionStore.createIndex('createdAtUnixMilliseconds', 'createdAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Repaired missing Worker session revision store');
            }

            if (!database.objectStoreNames.contains(STORE_ENGINE_SESSION_COMPONENTS)) {
                const componentStore = database.createObjectStore(
                    STORE_ENGINE_SESSION_COMPONENTS,
                    { keyPath: 'id' });
                componentStore.createIndex('createdAtUnixMilliseconds', 'createdAtUnixMilliseconds', { unique: false });
                console.log('[IndexedDB] Repaired missing Worker session component store');
            }
        };
        request.onsuccess = () => {
            resolve(attachDatabaseConnection(
                request.result,
                `[IndexedDB] Database opened successfully (v${request.result.version} - Trade store repair)`));
        };
    });
}

function requireTradeStore(database, storeName) {
    if (!database.objectStoreNames.contains(storeName)) {
        throw new Error(
            `[IndexedDB] Missing required Trade store "${storeName}". ` +
            `Opened database v${database.version}; app requested v${DB_VERSION}. ` +
            'Close other FFXIV Craft Architect tabs and reload so the browser can finish the storage upgrade.');
    }
}

async function getTradeStoreDiagnostics() {
    try {
        const database = await ensureTradeStores(await initDB());
        return createTradeStoreDiagnostics(database);
    } catch (error) {
        return createTradeStoreDiagnostics(null, formatIndexedDbError(error));
    }
}

function toPlanSummary(planData) {
    const metadata = planData?.storedPlanMetadata ?? planData;
    return {
        id: metadata.id,
        name: metadata.name || 'Saved Plan',
        modifiedAt: metadata.modifiedAt,
        savedAt: metadata.savedAt,
        dataCenter: metadata.dataCenter || 'Aether',
        itemCount: Array.isArray(metadata.projectItems) ? metadata.projectItems.length : 0
    };
}

function isComponentStoredPlan(record) {
    return record?.schemaVersion === STORED_PLAN_SCHEMA_VERSION &&
        record.storedPlanMetadata &&
        record.componentRefs;
}

function createStoredPlanComponent(planId, field, payload) {
    return {
        id: `${planId}:${field}:${crypto.randomUUID()}`,
        schemaVersion: 1,
        planId,
        field,
        payload
    };
}

function createStoredPlanRecord(planData, previousRecord = null, changedFields = null) {
    const data = {
        ...planData,
        savedAt: planData.savedAt || new Date().toISOString(),
        modifiedAt: planData.modifiedAt || new Date().toISOString()
    };
    const metadata = { ...data };
    for (const field of STORED_PLAN_COMPONENT_FIELDS) {
        delete metadata[field];
    }

    const canReuse = isComponentStoredPlan(previousRecord) && changedFields instanceof Set;
    const componentRefs = {};
    const components = [];
    for (const field of STORED_PLAN_COMPONENT_FIELDS) {
        if (canReuse && !changedFields.has(field)) {
            componentRefs[field] = previousRecord.componentRefs[field] ?? null;
            continue;
        }
        const payload = data[field] ?? null;
        if (payload === null) {
            componentRefs[field] = null;
            continue;
        }
        const component = createStoredPlanComponent(data.id, field, payload);
        componentRefs[field] = component.id;
        components.push(component);
    }

    return {
        record: {
            id: data.id,
            schemaVersion: STORED_PLAN_SCHEMA_VERSION,
            name: metadata.name,
            modifiedAt: metadata.modifiedAt,
            savedAt: metadata.savedAt,
            storedPlanMetadata: metadata,
            componentRefs
        },
        components
    };
}

function deleteReplacedPlanComponents(componentStore, previousRecord, successorRecord) {
    if (!isComponentStoredPlan(previousRecord)) {
        return;
    }
    const retained = new Set(
        Object.values(successorRecord.componentRefs).filter(id => typeof id === 'string'));
    for (const componentId of Object.values(previousRecord.componentRefs)) {
        if (typeof componentId === 'string' && !retained.has(componentId)) {
            componentStore.delete(componentId);
        }
    }
}

function persistStoredPlanSuccessor(transaction, previousRecord, successor) {
    const planStore = transaction.objectStore(STORE_PLANS);
    const componentStore = transaction.objectStore(STORE_PLAN_COMPONENTS);
    deleteReplacedPlanComponents(componentStore, previousRecord, successor.record);
    for (const component of successor.components) {
        componentStore.put(component);
    }
    planStore.put(successor.record);
    transaction.objectStore(STORE_PLAN_SUMMARIES).put(toPlanSummary(successor.record));
}

function materializeStoredPlanRecord(transaction, record, onmaterialized) {
    if (!record || !isComponentStoredPlan(record)) {
        onmaterialized(record || null);
        return;
    }

    const storedPlan = { ...record.storedPlanMetadata };
    const componentStore = transaction.objectStore(STORE_PLAN_COMPONENTS);
    const fields = STORED_PLAN_COMPONENT_FIELDS.filter(
        field => typeof record.componentRefs[field] === 'string');
    for (const field of STORED_PLAN_COMPONENT_FIELDS) {
        if (!record.componentRefs[field]) {
            storedPlan[field] = null;
        }
    }
    if (fields.length === 0) {
        onmaterialized(storedPlan);
        return;
    }

    let remaining = fields.length;
    for (const field of fields) {
        const request = componentStore.get(record.componentRefs[field]);
        request.onerror = () => transaction.abort();
        request.onsuccess = () => {
            const component = request.result;
            if (!component || component.planId !== record.id || component.field !== field) {
                transaction.abort();
                return;
            }
            storedPlan[field] = component.payload;
            remaining--;
            if (remaining === 0) {
                onmaterialized(storedPlan);
            }
        };
    }
}

/**
 * Save a plan to IndexedDB
 */
async function savePlan(planData) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS, STORE_PLAN_SUMMARIES],
            'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.get(planData.id);

        request.onerror = () => transaction.abort();
        request.onsuccess = () =>
            persistStoredPlanSuccessor(
                transaction,
                request.result,
                createStoredPlanRecord(planData));

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Load a plan by ID
 */
async function loadPlan(planId) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS],
            'readonly');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.get(planId);
        let materialized = null;

        request.onsuccess = () =>
            materializeStoredPlanRecord(transaction, request.result, value => {
                materialized = value;
            });
        request.onerror = () => transaction.abort();
        transaction.oncomplete = () => resolve(materialized);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) =>
            reject(transaction.error || event.target?.error || new Error('Saved plan is incomplete.'));
    });
}

async function patchStoredPlan(planId, planPatch) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS, STORE_PLAN_SUMMARIES],
            'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.get(planId);

        request.onerror = () => transaction.abort();
        request.onsuccess = () => {
            const previous = request.result;
            if (!previous) {
                resolve(false);
                return;
            }
            const base = isComponentStoredPlan(previous)
                ? previous.storedPlanMetadata
                : previous;
            const patch = { ...planPatch, modifiedAt: new Date().toISOString() };
            const changedFields = new Set(
                STORED_PLAN_COMPONENT_FIELDS.filter(field =>
                    Object.prototype.hasOwnProperty.call(patch, field)));
            persistStoredPlanSuccessor(
                transaction,
                previous,
                createStoredPlanRecord(
                    { ...base, ...patch },
                    previous,
                    changedFields));
        };

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Patch market analysis fields without transferring or rewriting the full plan payload.
 */
async function patchMarketAnalysis(
    planId,
    marketPlansJson,
    marketItemAnalysesJson,
    marketIntelligenceJson,
    recommendationMode,
    marketAnalysisLens,
    marketAnalysisRecipeBasisJson,
    marketAnalysisScopeSnapshotJson) {
    return await patchStoredPlan(planId, {
        marketPlansJson,
        marketIntelligenceJson,
        marketItemAnalysesJson,
        marketAnalysisRecipeBasisJson,
        marketAnalysisScopeSnapshotJson,
        procurementRouteJson: null,
        savedRecommendationMode: recommendationMode,
        savedMarketAnalysisLens: marketAnalysisLens
    });
}

/**
 * Patch plan decisions and the procurement route without transferring the
 * large market-evidence payload back through WebAssembly interop.
 */
async function patchPlanAndProcurementRoute(planId, planPatch) {
    return await patchStoredPlan(planId, planPatch);
}

/**
 * Load all plans (sorted by modified date, newest first)
 */
async function loadAllPlans() {
    const database = await initDB();
    const planIds = await new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS], 'readonly');
        const store = transaction.objectStore(STORE_PLANS);
        const index = store.index('modifiedAt');
        const request = index.openCursor(null, 'prev');
        
        const ids = [];
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                ids.push(cursor.primaryKey);
                cursor.continue();
            } else {
                resolve(ids);
            }
        };
        
        request.onerror = () => reject(request.error);
    });
    return await Promise.all(planIds.map(planId => loadPlan(planId)));
}

/**
 * Load plan summaries (sorted by modified date, newest first)
 */
async function loadPlanSummaries() {
    const database = await initDB();

    let summaries = await readPlanSummaries(database);
    const planCount = await countStoreRecords(database, STORE_PLANS);
    if (summaries.length >= planCount) {
        return summaries;
    }

    await rebuildPlanSummaries(database);
    return await readPlanSummaries(database);
}

async function saveStoreRecord(storeName, record) {
    let database = await initDB();
    database = await ensureTradeStores(database);
    requireTradeStore(database, storeName);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readwrite');
        const store = transaction.objectStore(storeName);
        store.put(record);

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function saveStoreRecordsBatch(storeName, records) {
    let database = await initDB();
    database = await ensureTradeStores(database);
    requireTradeStore(database, storeName);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readwrite');
        const store = transaction.objectStore(storeName);
        for (const record of records || []) {
            store.put(record);
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function loadStoreRecords(storeName) {
    let database = await initDB();
    database = await ensureTradeStores(database);
    requireTradeStore(database, storeName);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.openCursor();
        const records = [];

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                records.push(cursor.value);
                cursor.continue();
            } else {
                resolve(records);
            }
        };

        request.onerror = () => reject(request.error);
    });
}

async function loadStoreRecord(storeName, id) {
    let database = await initDB();
    database = await ensureTradeStores(database);
    requireTradeStore(database, storeName);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.get(id);

        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
    });
}

async function deleteStoreRecord(storeName, id) {
    let database = await initDB();
    database = await ensureTradeStores(database);
    requireTradeStore(database, storeName);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readwrite');
        const store = transaction.objectStore(storeName);
        store.delete(id);

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function saveTradeCompanyProfile(profile) {
    return await saveStoreRecord(STORE_TRADE_COMPANY_PROFILES, profile);
}

async function loadTradeCompanyProfiles() {
    const profiles = await loadStoreRecords(STORE_TRADE_COMPANY_PROFILES);
    return profiles.sort((a, b) => String(b.updatedAtUtc || '').localeCompare(String(a.updatedAtUtc || '')));
}

async function saveTradeCrafter(crafter) {
    return await saveStoreRecord(STORE_TRADE_CRAFTERS, crafter);
}

async function saveTradeCraftersBatch(crafters) {
    return await saveStoreRecordsBatch(STORE_TRADE_CRAFTERS, crafters);
}

async function loadTradeCrafters(companyProfileId) {
    const crafters = await loadStoreRecords(STORE_TRADE_CRAFTERS);
    return crafters
        .filter(crafter => crafter.companyProfileId === companyProfileId)
        .sort((a, b) => String(a.displayName || '').localeCompare(String(b.displayName || '')));
}

async function saveTradeOrder(order) {
    return await saveStoreRecord(STORE_TRADE_ORDERS, order);
}

async function saveTradeOrdersBatch(orders) {
    return await saveStoreRecordsBatch(STORE_TRADE_ORDERS, orders);
}

async function loadTradeOrders(companyProfileId) {
    const orders = await loadStoreRecords(STORE_TRADE_ORDERS);
    return orders
        .filter(order => order.companyProfileId === companyProfileId)
        .sort((a, b) => String(b.commissionedAtUtc || '').localeCompare(String(a.commissionedAtUtc || '')));
}

async function deleteTradeOrder(orderId) {
    return await deleteStoreRecord(STORE_TRADE_ORDERS, orderId);
}

async function saveTradeOrderCraftSnapshot(snapshot) {
    return await saveStoreRecord(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, snapshot);
}

async function loadTradeOrderCraftSnapshot(snapshotId) {
    return await loadStoreRecord(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, snapshotId);
}

async function loadTradeOrderCraftSnapshotsForCompany(companyProfileId) {
    const snapshots = await loadStoreRecords(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS);
    return snapshots
        .filter(snapshot => snapshot.companyProfileId === companyProfileId)
        .sort((a, b) => String(b.updatedAtUtc || '').localeCompare(String(a.updatedAtUtc || '')));
}

async function deleteTradeOrderCraftSnapshot(snapshotId) {
    return await deleteStoreRecord(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, snapshotId);
}

async function saveTradePayrollDraft(draft) {
    return await saveStoreRecord(STORE_TRADE_PAYROLL_DRAFTS, draft);
}

async function saveTradePayrollDraftsBatch(drafts) {
    return await saveStoreRecordsBatch(STORE_TRADE_PAYROLL_DRAFTS, drafts);
}

async function loadTradePayrollDrafts(companyProfileId) {
    const drafts = await loadStoreRecords(STORE_TRADE_PAYROLL_DRAFTS);
    return drafts
        .filter(draft => draft.companyProfileId === companyProfileId)
        .sort((a, b) => String(b.updatedAtUtc || '').localeCompare(String(a.updatedAtUtc || '')));
}

async function deleteTradePayrollDraft(draftId) {
    return await deleteStoreRecord(STORE_TRADE_PAYROLL_DRAFTS, draftId);
}

async function readPlanSummaries(database) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLAN_SUMMARIES], 'readonly');
        const store = transaction.objectStore(STORE_PLAN_SUMMARIES);
        const index = store.index('modifiedAt');
        const request = index.openCursor(null, 'prev');

        const summaries = [];

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                summaries.push(cursor.value);
                cursor.continue();
            } else {
                resolve(summaries);
            }
        };

        request.onerror = () => reject(request.error);
    });
}

async function countStoreRecords(database, storeName) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([storeName], 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.count();

        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function rebuildPlanSummaries(database) {
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const planStore = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        const clearRequest = summaryStore.clear();

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);

        clearRequest.onsuccess = () => {
            const request = planStore.openCursor();
            request.onsuccess = (event) => {
                const cursor = event.target.result;
                if (!cursor) {
                    return;
                }

                summaryStore.put(toPlanSummary(cursor.value));
                cursor.continue();
            };
        };
    });
}

/**
 * Delete a plan by ID
 */
async function deletePlan(planId) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS, STORE_PLAN_SUMMARIES],
            'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const componentStore = transaction.objectStore(STORE_PLAN_COMPONENTS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        const request = store.get(planId);

        request.onerror = () => transaction.abort();
        request.onsuccess = () => {
            for (const componentId of Object.values(request.result?.componentRefs ?? {})) {
                if (typeof componentId === 'string') {
                    componentStore.delete(componentId);
                }
            }
            store.delete(planId);
            summaryStore.delete(planId);
        };

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Save a setting
 */
async function saveSetting(key, value) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_SETTINGS], 'readwrite');
        const store = transaction.objectStore(STORE_SETTINGS);
        store.put({ key, value });

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Load a setting
 */
async function loadSetting(key) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_SETTINGS], 'readonly');
        const store = transaction.objectStore(STORE_SETTINGS);
        const request = store.get(key);
        
        request.onsuccess = () => {
            const result = request.result;
            resolve(result ? result.value : null);
        };
        request.onerror = () => reject(request.error);
    });
}

async function loadAllSettings() {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_SETTINGS], 'readonly');
        const store = transaction.objectStore(STORE_SETTINGS);
        const request = store.openCursor();
        const settings = {};

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                settings[cursor.value.key] = cursor.value.value;
                cursor.continue();
            } else {
                resolve(settings);
            }
        };
        request.onerror = () => reject(request.error);
    });
}

async function saveSettingsBatch(settings) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_SETTINGS], 'readwrite');
        const store = transaction.objectStore(STORE_SETTINGS);
        for (const [key, value] of Object.entries(settings || {})) {
            store.put({ key, value });
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function savePlansBatch(plans) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS, STORE_PLAN_SUMMARIES],
            'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        for (const plan of plans || []) {
            const request = store.get(plan.id);
            request.onerror = () => transaction.abort();
            request.onsuccess = () =>
                persistStoredPlanSuccessor(
                    transaction,
                    request.result,
                    createStoredPlanRecord(plan));
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Clear all plans
 */
async function clearAllPlans() {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS, STORE_PLAN_SUMMARIES],
            'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const componentStore = transaction.objectStore(STORE_PLAN_COMPONENTS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        store.clear();
        componentStore.clear();
        summaryStore.clear();

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Clear entire market cache
 */
async function clearMarketCache() {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.clear();
        
        request.onsuccess = () => {
            console.log('[IndexedDB] Cleared entire market cache');
            resolve(true);
        };
        request.onerror = () => reject(request.error);
    });
}

/**
 * Save market data to cache (using Unix timestamp)
 */
async function saveMarketData(key, data) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        
        // Use Unix timestamp (seconds since epoch) for safe serialization
        const cacheEntry = {
            key: key,
            itemId: data.itemId,
            dataCenter: data.dataCenter,
            fetchedAtUnix: data.fetchedAtUnix,  // Unix timestamp in seconds
            lastUploadTimeUnixMilliseconds: data.lastUploadTimeUnixMilliseconds,
            dcAvgPrice: data.dcAvgPrice,
            hqAvgPrice: data.hqAvgPrice,
            worlds: data.worlds
        };
        
        const request = store.put(cacheEntry);
        
        request.onsuccess = () => {
            console.log('[IndexedDB] Saved market data for', key, 'timestamp:', cacheEntry.fetchedAtUnix);
            resolve(true);
        };
        request.onerror = () => {
            console.error('[IndexedDB] Failed to save market data:', request.error);
            reject(request.error);
        };
    });
}

/**
 * Save multiple market data entries to cache in one transaction.
 * Entries are objects shaped as { key, data } from IndexedDbMarketCacheService.
 */
async function saveMarketDataBatch(entries) {
    const database = await initDB();
    const batchEntries = entries || [];

    if (batchEntries.length === 0) {
        return true;
    }

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);

        transaction.oncomplete = () => {
            console.log('[IndexedDB] Saved market data batch:', batchEntries.length);
            resolve(true);
        };
        transaction.onerror = () => {
            console.error('[IndexedDB] Failed to save market data batch:', transaction.error);
            reject(transaction.error);
        };
        transaction.onabort = () => {
            console.error('[IndexedDB] Market data batch transaction aborted:', transaction.error);
            reject(transaction.error);
        };

        for (const batchEntry of batchEntries) {
            const data = batchEntry?.data ?? batchEntry?.Data;
            const key = batchEntry?.key ?? batchEntry?.Key ?? data?.key;
            if (!key || !data) {
                transaction.abort();
                reject(new Error('Invalid market data batch entry.'));
                return;
            }

            const cacheEntry = {
                key: key,
                itemId: data.itemId,
                dataCenter: data.dataCenter,
                fetchedAtUnix: data.fetchedAtUnix,
                lastUploadTimeUnixMilliseconds: data.lastUploadTimeUnixMilliseconds,
                dcAvgPrice: data.dcAvgPrice,
                hqAvgPrice: data.hqAvgPrice,
                worlds: data.worlds
            };

            store.put(cacheEntry);
        }
    });
}

/**
 * Load market data from cache
 * Normalizes old format (fetchedAt string) to new format (fetchedAtUnix number)
 */
async function loadMarketData(key) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.get(key);
        
        request.onsuccess = () => {
            const result = request.result;
            if (result) {
                // Normalize old format to new format
                const unix = getFetchedAtUnix(result);
                if (unix > 0) {
                    result.fetchedAtUnix = unix;
                }
                console.log('[IndexedDB] Loaded market data for', key, 'timestamp:', result.fetchedAtUnix);
            }
            resolve(result || null);
        };
        request.onerror = () => {
            console.error('[IndexedDB] Failed to load market data:', request.error);
            reject(request.error);
        };
    });
}

/**
 * Load freshness metadata (key + fetchedAtUnix) for market cache entries.
 * Freshness probes only need timestamps; this keeps full world/listing payloads
 * off the JS interop boundary when no market data has to be deserialized.
 * @param {string[]} keys - Market cache keys in itemId@dataCenter format
 */
async function getMarketDataFreshness(keys) {
    const database = await initDB();
    const uniqueKeys = Array.from(new Set(keys || []));

    if (uniqueKeys.length === 0) {
        return [];
    }

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const results = [];

        transaction.oncomplete = () => {
            resolve(results);
        };
        transaction.onerror = () => {
            console.error('[IndexedDB] Failed to load market data freshness:', transaction.error);
            reject(transaction.error);
        };
        transaction.onabort = () => {
            console.error('[IndexedDB] Freshness load transaction aborted:', transaction.error);
            reject(transaction.error);
        };

        for (const key of uniqueKeys) {
            const request = store.get(key);

            request.onsuccess = () => {
                const result = request.result;
                if (!result) {
                    return;
                }

                results.push({ key, fetchedAtUnix: getFetchedAtUnix(result) });
            };
            request.onerror = () => {
                console.error('[IndexedDB] Failed to load freshness for', key, request.error);
                reject(request.error);
            };
        }
    });
}


/**
 * Load multiple fresh market cache entries in one IndexedDB transaction.
 * Missing or stale entries are omitted from the returned array.
 * @param {string[]} keys - Market cache keys in itemId@dataCenter format
 * @param {number} cutoffUnix - Unix timestamp in seconds; entries older than this are stale
 */
async function loadMarketDataBulk(keys, cutoffUnix) {
    const database = await initDB();
    const uniqueKeys = Array.from(new Set(keys || []));

    if (uniqueKeys.length === 0) {
        return [];
    }

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const results = [];

        transaction.oncomplete = () => {
            console.log('[IndexedDB] Bulk loaded market data:', results.length, 'of', uniqueKeys.length);
            resolve(results);
        };
        transaction.onerror = () => {
            console.error('[IndexedDB] Failed to bulk load market data:', transaction.error);
            reject(transaction.error);
        };
        transaction.onabort = () => {
            console.error('[IndexedDB] Bulk load transaction aborted:', transaction.error);
            reject(transaction.error);
        };

        for (const key of uniqueKeys) {
            const request = store.get(key);

            request.onsuccess = () => {
                const result = request.result;
                if (!result) {
                    return;
                }

                const unix = getFetchedAtUnix(result);
                if (unix <= cutoffUnix) {
                    return;
                }

                result.fetchedAtUnix = unix;
                results.push(result);
            };
            request.onerror = () => {
                console.error('[IndexedDB] Failed to bulk load market data for', key, request.error);
                reject(request.error);
            };
        }
    });
}

/**
 * Helper to get Unix timestamp from entry (handles both old and new formats)
 */
function getFetchedAtUnix(entry) {
    // New format: Unix timestamp (number)
    if (typeof entry.fetchedAtUnix === 'number') {
        return entry.fetchedAtUnix;
    }
    // Old format: ISO date string
    if (typeof entry.fetchedAt === 'string') {
        try {
            return Math.floor(new Date(entry.fetchedAt).getTime() / 1000);
        } catch (e) {
            console.warn('[IndexedDB] Invalid date format:', entry.fetchedAt);
            return 0;
        }
    }
    return 0;
}

/**
 * Delete stale market data using Unix timestamp cutoff
 * @param {number} cutoffUnix - Unix timestamp in seconds (entries older than this are deleted)
 */
async function deleteStaleMarketData(cutoffUnix) {
    const database = await initDB();

    console.log('[IndexedDB] Deleting stale entries through timestamp index up to:', cutoffUnix);

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const index = store.index('fetchedAtUnix');
        const request = index.openKeyCursor(IDBKeyRange.upperBound(cutoffUnix));
        let deletedCount = 0;
        let settled = false;

        transaction.oncomplete = () => {
            settled = true;
            if (deletedCount > 0) {
                console.log('[IndexedDB] Deleted', deletedCount, 'stale entries');
            }
            resolve(deletedCount);
        };
        transaction.onerror = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        transaction.onabort = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        request.onerror = () => transaction.abort();
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor) return;
            store.delete(cursor.primaryKey);
            deletedCount++;
            cursor.continue();
        };
    });
}

/**
 * Delete oldest N entries (LRU eviction)
 * @param {number} count - Number of entries to delete
 */
async function deleteOldestEntries(count) {
    const database = await initDB();
    const requestedCount = Math.max(0, Math.floor(count || 0));
    if (requestedCount === 0) return 0;

    console.log('[IndexedDB] Deleting', requestedCount, 'oldest indexed entries');

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.index('fetchedAtUnix').openKeyCursor();
        let deletedCount = 0;
        let settled = false;

        transaction.oncomplete = () => {
            settled = true;
            console.log('[IndexedDB] Deleted', deletedCount, 'oldest entries');
            resolve(deletedCount);
        };
        transaction.onerror = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        transaction.onabort = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        request.onerror = () => transaction.abort();
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor || deletedCount >= requestedCount) return;
            store.delete(cursor.primaryKey);
            deletedCount++;
            cursor.continue();
        };
    });
}

/**
 * Delete records that are absent from the timestamp index without reading their payloads.
 * This legacy repair runs only when cache limits are already exceeded.
 */
async function deleteUnindexedMarketData(count) {
    const database = await initDB();
    const requestedCount = Math.max(0, Math.floor(count || 0));
    if (requestedCount === 0) return 0;

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const allKeysRequest = store.getAllKeys();
        const indexedKeysRequest = store.index('fetchedAtUnix').getAllKeys();
        let deletedCount = 0;
        let settled = false;

        transaction.oncomplete = () => {
            settled = true;
            resolve(deletedCount);
        };
        transaction.onerror = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        transaction.onabort = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        indexedKeysRequest.onsuccess = () => {
            if (allKeysRequest.readyState !== 'done') return;
            const indexedKeys = new Set(indexedKeysRequest.result);
            for (const key of allKeysRequest.result) {
                if (!indexedKeys.has(key) && deletedCount < requestedCount) {
                    store.delete(key);
                    deletedCount++;
                }
            }
        };
        allKeysRequest.onsuccess = () => {
            if (indexedKeysRequest.readyState !== 'done') return;
            const indexedKeys = new Set(indexedKeysRequest.result);
            for (const key of allKeysRequest.result) {
                if (!indexedKeys.has(key) && deletedCount < requestedCount) {
                    store.delete(key);
                    deletedCount++;
                }
            }
        };
    });
}

/**
 * Get market cache statistics using Unix timestamps
 * @param {number} cutoffUnix - Unix timestamp for determining staleness (entries newer than this are valid)
 */
async function getMarketCacheStats(cutoffUnix) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const index = store.index('fetchedAtUnix');
        const totalRequest = store.count();
        const indexedRequest = index.count();
        const staleRequest = index.count(IDBKeyRange.upperBound(cutoffUnix));
        const oldestRequest = index.openKeyCursor();
        const newestRequest = index.openKeyCursor(null, 'prev');
        let settled = false;

        transaction.oncomplete = () => {
            settled = true;
            const total = totalRequest.result;
            const indexed = indexedRequest.result;
            const stale = staleRequest.result;
            const legacyUnindexed = Math.max(0, total - indexed);
            const stats = {
                total,
                valid: Math.max(0, indexed - stale),
                stale,
                legacyUnindexed,
                oldestUnix: oldestRequest.result?.key || 0,
                newestUnix: newestRequest.result?.key || 0,
                // A conservative fixed-per-entry policy is stable and bounded. It avoids
                // cloning or serializing listing payloads merely to enforce cache limits.
                sizeBytes: total * APPROXIMATE_MARKET_ENTRY_BYTES
            };
            console.log('[IndexedDB] Cache stats:', stats);
            resolve(stats);
        };
        transaction.onerror = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
        transaction.onabort = (event) => {
            if (!settled) reject(transaction.error || event.target?.error);
        };
    });
}

async function claimEngineTransaction(transactionId, canonicalRequestHash) {
    requireEngineLedgerIdentity(transactionId, canonicalRequestHash);
    const lockAcquired = await acquireEngineTransactionLock(transactionId);
    if (!lockAcquired) {
        const existing = await readEngineTransaction(transactionId);
        if (!existing) {
            return {
                disposition: 'activeReplay',
                canonicalRequestHash,
                terminalResultJson: null,
                claimToken: null
            };
        }
        return createEngineTransactionClaim(existing, canonicalRequestHash, false);
    }

    try {
        const database = await initDB();
        const result = await new Promise((resolve, reject) => {
            const transaction = database.transaction(STORE_ENGINE_TRANSACTIONS, 'readwrite');
            const store = transaction.objectStore(STORE_ENGINE_TRANSACTIONS);
            const request = store.get(transactionId);
            let claim;
            request.onsuccess = () => {
                const existing = request.result;
                if (existing && existing.canonicalRequestHash !== canonicalRequestHash) {
                    claim = createEngineTransactionClaim(existing, canonicalRequestHash, true);
                    return;
                }
                if (existing?.terminalResultJson || existing?.terminalExpired) {
                    claim = createEngineTransactionClaim(existing, canonicalRequestHash, true);
                    return;
                }

                const claimToken = crypto.randomUUID().replaceAll('-', '');
                const disposition = existing ? 'abandonedReplay' : 'claimed';
                store.put({
                    transactionId,
                    canonicalRequestHash,
                    claimToken,
                    terminalResultJson: null,
                    updatedAtUnixMilliseconds: Date.now()
                });
                claim = {
                    disposition,
                    canonicalRequestHash,
                    terminalResultJson: null,
                    claimToken
                };
            };
            request.onerror = () => reject(request.error);
            transaction.oncomplete = () => resolve(claim);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error);
        });

        if (result.claimToken) {
            engineTransactionLocks.get(transactionId).claimToken = result.claimToken;
            return result;
        }
        releaseEngineTransactionLock(transactionId);
        return result;
    } catch (error) {
        releaseEngineTransactionLock(transactionId);
        throw error;
    }
}

async function completeEngineTransaction(
    transactionId,
    canonicalRequestHash,
    claimToken,
    terminalResultJson) {
    requireEngineLedgerIdentity(transactionId, canonicalRequestHash, claimToken);
    if (!terminalResultJson) throw new Error('A terminal engine result is required.');
    const heldLock = engineTransactionLocks.get(transactionId);
    if (!heldLock || heldLock.claimToken !== claimToken) {
        throw new Error('The engine transaction lock is not owned by this claim.');
    }
    const database = await initDB();
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_ENGINE_TRANSACTIONS, 'readwrite');
        const store = transaction.objectStore(STORE_ENGINE_TRANSACTIONS);
        const request = store.get(transactionId);
        request.onsuccess = () => {
            const existing = request.result;
            if (!existing ||
                existing.canonicalRequestHash !== canonicalRequestHash ||
                existing.claimToken !== claimToken) {
                transaction.abort();
                reject(new Error('The engine transaction claim no longer matches its canonical identity.'));
                return;
            }
            if (existing.terminalResultJson && existing.terminalResultJson !== terminalResultJson) {
                transaction.abort();
                reject(new Error('The engine transaction already contains a different terminal result.'));
                return;
            }
            existing.terminalResultJson ||= terminalResultJson;
            existing.terminalExpired = false;
            const completedAt = Date.now();
            existing.updatedAtUnixMilliseconds = completedAt;
            existing.terminalUpdatedAtUnixMilliseconds = completedAt;
            existing.retentionSchemaVersion = ENGINE_TERMINAL_RETENTION_SCHEMA;
            const put = store.put(existing);
            put.onsuccess = () => expireTerminalPayloads(
                store,
                store.index('terminalUpdatedAtUnixMilliseconds'),
                ENGINE_TERMINAL_RETENTION_LIMIT);
        };
        request.onerror = () => reject(request.error);
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error || new Error('Engine transaction completion aborted.'));
    });
    releaseEngineTransactionLock(transactionId, claimToken);
}

function backfillEngineTerminalRetention(store, createIndex) {
    const request = store.openCursor();
    request.onsuccess = () => {
        const cursor = request.result;
        if (!cursor) {
            expireTerminalPayloads(store, createIndex(), ENGINE_TERMINAL_RETENTION_LIMIT);
            return;
        }
        const value = cursor.value;
        if (value.terminalResultJson && !value.terminalUpdatedAtUnixMilliseconds) {
            value.terminalUpdatedAtUnixMilliseconds = value.updatedAtUnixMilliseconds || Date.now();
            value.retentionSchemaVersion = ENGINE_TERMINAL_RETENTION_SCHEMA;
            cursor.update(value);
        }
        cursor.continue();
    };
}

function expireTerminalPayloads(store, index, maximumRetained) {
    const request = index.openCursor(null, 'prev');
    let retained = 0;
    request.onsuccess = () => {
        const cursor = request.result;
        if (!cursor) return;
        const value = cursor.value;
        if (value.terminalResultJson) {
            if (retained < maximumRetained) {
                retained++;
            } else {
                value.terminalResultJson = null;
                value.terminalExpired = true;
                delete value.terminalUpdatedAtUnixMilliseconds;
                cursor.update(value);
            }
        }
        cursor.continue();
    };
}

async function releaseEngineTransaction(transactionId, canonicalRequestHash, claimToken) {
    requireEngineLedgerIdentity(transactionId, canonicalRequestHash, claimToken);
    await mutateClaimedEngineTransaction(
        transactionId,
        canonicalRequestHash,
        claimToken,
        existing => {
            if (existing.terminalResultJson || existing.terminalExpired) {
                throw new Error('A terminal engine transaction cannot be released.');
            }
            return null;
        });
    releaseEngineTransactionLock(transactionId, claimToken);
}

async function mutateClaimedEngineTransaction(
    transactionId,
    canonicalRequestHash,
    claimToken,
    mutate) {
    const heldLock = engineTransactionLocks.get(transactionId);
    if (!heldLock || heldLock.claimToken !== claimToken) {
        throw new Error('The engine transaction lock is not owned by this claim.');
    }
    const database = await initDB();
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_ENGINE_TRANSACTIONS, 'readwrite');
        const store = transaction.objectStore(STORE_ENGINE_TRANSACTIONS);
        const request = store.get(transactionId);
        request.onsuccess = () => {
            const existing = request.result;
            if (!existing ||
                existing.canonicalRequestHash !== canonicalRequestHash ||
                existing.claimToken !== claimToken) {
                transaction.abort();
                reject(new Error('The engine transaction claim no longer matches its canonical identity.'));
                return;
            }
            try {
                const updated = mutate(existing);
                if (updated) store.put(updated);
                else store.delete(transactionId);
            } catch (error) {
                transaction.abort();
                reject(error);
            }
        };
        request.onerror = () => reject(request.error);
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error || new Error('Engine transaction mutation aborted.'));
    });
}

async function readEngineTransaction(transactionId) {
    const database = await initDB();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_ENGINE_TRANSACTIONS, 'readonly');
        const request = transaction.objectStore(STORE_ENGINE_TRANSACTIONS).get(transactionId);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
    });
}

function createEngineTransactionClaim(existing, requestedHash, ownsLock) {
    if (existing.canonicalRequestHash !== requestedHash) {
        return {
            disposition: 'conflict',
            canonicalRequestHash: existing.canonicalRequestHash,
            terminalResultJson: null,
            claimToken: null
        };
    }
    if (existing.terminalResultJson) {
        return {
            disposition: 'terminalReplay',
            canonicalRequestHash: existing.canonicalRequestHash,
            terminalResultJson: existing.terminalResultJson,
            claimToken: null
        };
    }
    if (existing.terminalExpired) {
        return {
            disposition: 'expiredTerminalReplay',
            canonicalRequestHash: existing.canonicalRequestHash,
            terminalResultJson: null,
            claimToken: null
        };
    }
    return {
        disposition: ownsLock ? 'abandonedReplay' : 'activeReplay',
        canonicalRequestHash: existing.canonicalRequestHash,
        terminalResultJson: null,
        claimToken: ownsLock ? existing.claimToken : null
    };
}

async function acquireEngineTransactionLock(transactionId) {
    if (!navigator.locks) {
        throw new Error('The Web Locks API is required for durable engine transaction ownership.');
    }
    if (engineTransactionLocks.has(transactionId)) return false;

    let release;
    let reportAcquisition;
    const held = new Promise(resolve => { release = resolve; });
    const acquired = new Promise(resolve => { reportAcquisition = resolve; });
    const lockTask = navigator.locks.request(
        `craft-architect-engine:${transactionId}`,
        { mode: 'exclusive', ifAvailable: true },
        async lock => {
            reportAcquisition(Boolean(lock));
            if (lock) await held;
        });
    const available = await acquired;
    if (!available) {
        await lockTask;
        return false;
    }
    engineTransactionLocks.set(transactionId, { release, lockTask, claimToken: null });
    return true;
}

function releaseEngineTransactionLock(transactionId, claimToken = null) {
    const held = engineTransactionLocks.get(transactionId);
    if (!held || claimToken && held.claimToken !== claimToken) return false;
    engineTransactionLocks.delete(transactionId);
    held.release();
    return true;
}

function requireEngineLedgerIdentity(transactionId, canonicalRequestHash, claimToken = null) {
    if (!transactionId || !canonicalRequestHash || claimToken === '') {
        throw new Error('Complete engine transaction identity is required.');
    }
}

// Export functions for Blazor interop
window.IndexedDB = {
    moduleRevision: MODULE_REVISION,
    schemaVersion: DB_VERSION,
    savePlan,
    loadPlan,
    loadAllPlans,
    loadPlanSummaries,
    savePlansBatch,
    patchMarketAnalysis,
    patchPlanAndProcurementRoute,
    deletePlan,
    saveSetting,
    loadSetting,
    loadAllSettings,
    saveSettingsBatch,
    clearAllPlans,
    clearMarketCache,
    saveMarketData,
    saveMarketDataBatch,
    loadMarketData,
    loadMarketDataBulk,
    getMarketDataFreshness,
    deleteStaleMarketData,
    deleteOldestEntries,
    deleteUnindexedMarketData,
    getMarketCacheStats,
    saveTradeCompanyProfile,
    loadTradeCompanyProfiles,
    saveTradeCrafter,
    saveTradeCraftersBatch,
    loadTradeCrafters,
    saveTradeOrder,
    saveTradeOrdersBatch,
    loadTradeOrders,
    deleteTradeOrder,
    saveTradeOrderCraftSnapshot,
    loadTradeOrderCraftSnapshot,
    loadTradeOrderCraftSnapshotsForCompany,
    deleteTradeOrderCraftSnapshot,
    saveTradePayrollDraft,
    saveTradePayrollDraftsBatch,
    loadTradePayrollDrafts,
    deleteTradePayrollDraft,
    getTradeStoreDiagnostics,
    claimEngineTransaction,
    completeEngineTransaction,
    releaseEngineTransaction
};

console.log(`[IndexedDB] Module loaded (revision ${MODULE_REVISION}, schema ${DB_VERSION})`);
