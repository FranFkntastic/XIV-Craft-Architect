// IndexedDB module for FFXIV Craft Architect Web
// Uses Unix timestamps (seconds since epoch) for serialization safety
// Database separation expresses authority and lifecycle only. Same-origin scripts
// can access every database, and IndexedDB does not synchronize branches or origins.

const LEGACY_DB_NAME = 'FFXIVCraftArchitect';
const LEGACY_DB_VERSION = 15;
const PERSONAL_DB_NAME = 'FFXIVCraftArchitect.Personal';
const PERSONAL_DB_VERSION = 1;
const MARKET_DB_NAME = 'FFXIVCraftArchitect.Market';
const MARKET_DB_VERSION = 1;
const COMPANY_DB_NAME = 'FFXIVCraftArchitect.Company';
const COMPANY_DB_VERSION = 1;
const ENGINE_DB_NAME = 'FFXIVCraftArchitect.Engine';
const ENGINE_DB_VERSION = 1;
const DB_NAME = LEGACY_DB_NAME;
// Retained as the public compatibility value while callers move to schemaVersions.
const DB_VERSION = LEGACY_DB_VERSION;
const MODULE_REVISION = 21;
const APPROXIMATE_MARKET_ENTRY_BYTES = 256 * 1024;
const STORE_STORAGE_METADATA = 'storageMetadata';
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
const STORE_ENGINE_SESSION_MANIFESTS = 'engineSessionManifests';
const STORE_ENGINE_SESSION_REVISIONS = 'engineSessionRevisions';
const STORE_ENGINE_SESSION_COMPONENTS = 'engineSessionComponents';
const STORE_COMPANY_IDENTITIES = 'companyIdentities';
const STORE_COMPANY_RECORDS = 'companyRecords';
const STORE_COMPANY_MUTATION_OUTBOX = 'companyMutationOutbox';
const STORE_PORTABLE_SETTINGS = 'portableOperatorSettings';
const LEGACY_MIGRATION_ID = 'legacy-monolith-v15';
const PORTABLE_SETTINGS_SCHEMA_VERSION = 1;
const TRADE_COMPANY_PROTOCOL_VERSION = 1;
const TRADE_COMPANY_RECORD_KINDS = Object.freeze([
    'profile',
    'crafter',
    'order',
    'payroll',
    'planArtifact',
    'publication',
    'collaboration',
    'operatorSettings'
]);
const STORED_PLAN_SCHEMA_VERSION = 2;
const STORED_PLAN_COMPONENT_FIELDS = Object.freeze([
    'planJson',
    'planStateJson',
    'marketPlansJson',
    'marketIntelligenceJson',
    'marketItemAnalysesJson',
    'marketAnalysisRecipeBasisJson',
    'marketAnalysisScopeSnapshotJson',
    'procurementRouteJson'
]);

let db = null;
let personalDb = null;
let marketDb = null;
let companyDb = null;
let personalInitialization = null;
let marketInitialization = null;
let companyInitialization = null;

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
async function initLegacyDB() {
    if (db) return db;
    
    return new Promise((resolve, reject) => {
        let blocked = false;
        const request = indexedDB.open(DB_NAME, LEGACY_DB_VERSION);
        
        request.onerror = () => {
            if (request.error?.name === 'VersionError') {
                reject(new Error(
                    `[IndexedDB] ${LEGACY_DB_NAME} uses a newer incompatible schema than the migration reader ` +
                    `(maximum supported v${LEGACY_DB_VERSION}).`));
                return;
            }

            reject(request.error);
        };
        request.onblocked = () => {
            blocked = true;
            const message = '[IndexedDB] Database upgrade blocked by another open tab. Close other FFXIV Craft Architect tabs and reload.';
            console.warn(message);
            reject(new Error(message));
        };
        request.onsuccess = () => {
            if (blocked) {
                request.result.close();
                return;
            }
            const database = attachDatabaseConnection(
                request.result,
                `[IndexedDB] Database opened successfully (schema v${request.result.version}; module r${MODULE_REVISION})`);
            if (!hasRequiredLegacyStores(database)) {
                db = null;
                database.close();
                reject(new Error(
                    `[IndexedDB] ${LEGACY_DB_NAME} v${database.version} is missing required migration stores. ` +
                    'The legacy database is incompatible and was left unchanged.'));
                return;
            }
            resolve(database);
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

const LEGACY_SNAPSHOT_STORES = Object.freeze([
    STORE_PLANS,
    STORE_PLAN_COMPONENTS,
    STORE_PLAN_SUMMARIES,
    STORE_SETTINGS,
    STORE_MARKET_CACHE,
    STORE_TRADE_COMPANY_PROFILES,
    STORE_TRADE_CRAFTERS,
    STORE_TRADE_ORDERS,
    STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
    STORE_TRADE_PAYROLL_DRAFTS
]);

let legacySnapshotInitialization = null;
let legacySnapshotSourceVersion = null;

async function databaseExists(name) {
    if (typeof indexedDB.databases !== 'function') {
        throw new Error(
            '[IndexedDB] This browser cannot enumerate databases, so the legacy migration cannot run safely.');
    }

    const databases = await indexedDB.databases();
    return databases.some(database => database.name === name);
}

function openSpecializedDatabase(name, version, upgrade) {
    return new Promise((resolve, reject) => {
        let blocked = false;
        const request = indexedDB.open(name, version);
        request.onerror = () => {
            if (request.error?.name === 'VersionError') {
                reject(new Error(
                    `[IndexedDB] ${name} uses a newer incompatible schema than this build supports (expected v${version}).`));
                return;
            }
            reject(request.error);
        };
        request.onblocked = () => {
            blocked = true;
            reject(new Error(
                `[IndexedDB] ${name} upgrade is blocked by another Craft Architect tab. ` +
                'The stale tab must release this database before migration can continue.'));
        };
        request.onupgradeneeded = event => upgrade(
            event.target.result,
            event.target.transaction,
            event.oldVersion || 0);
        request.onsuccess = () => {
            const database = request.result;
            if (blocked) {
                database.close();
                return;
            }
            if (database.version !== version) {
                database.close();
                reject(new Error(
                    `[IndexedDB] ${name} opened at incompatible schema v${database.version}; expected v${version}.`));
                return;
            }
            const incompatibility = specializedSchemaIncompatibility(database);
            if (incompatibility) {
                database.close();
                reject(new Error(
                    `[IndexedDB] ${name} v${version} has an incompatible schema: ${incompatibility}.`));
                return;
            }
            database.onversionchange = () => database.close();
            resolve(database);
        };
    });
}

function specializedSchemaIncompatibility(database) {
    const requiredStores = {
        [PERSONAL_DB_NAME]: [
            STORE_STORAGE_METADATA,
            STORE_PLANS,
            STORE_PLAN_COMPONENTS,
            STORE_PLAN_SUMMARIES,
            STORE_SETTINGS
        ],
        [MARKET_DB_NAME]: [STORE_STORAGE_METADATA, STORE_MARKET_CACHE],
        [COMPANY_DB_NAME]: [
            STORE_STORAGE_METADATA,
            STORE_TRADE_COMPANY_PROFILES,
            STORE_TRADE_CRAFTERS,
            STORE_TRADE_ORDERS,
            STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
            STORE_TRADE_PAYROLL_DRAFTS,
            STORE_COMPANY_IDENTITIES,
            STORE_COMPANY_RECORDS,
            STORE_COMPANY_MUTATION_OUTBOX,
            STORE_PORTABLE_SETTINGS
        ]
    }[database.name] ?? [];
    const missingStores = requiredStores.filter(
        storeName => !database.objectStoreNames.contains(storeName));
    if (missingStores.length > 0) {
        return `missing stores ${missingStores.join(', ')}`;
    }

    const requiredIndexes = {
        [PERSONAL_DB_NAME]: {
            [STORE_PLANS]: ['name', 'modifiedAt'],
            [STORE_PLAN_COMPONENTS]: ['planId'],
            [STORE_PLAN_SUMMARIES]: ['name', 'modifiedAt', 'savedAt']
        },
        [MARKET_DB_NAME]: {
            [STORE_MARKET_CACHE]: ['fetchedAtUnix']
        },
        [COMPANY_DB_NAME]: {
            [STORE_TRADE_COMPANY_PROFILES]: ['updatedAtUtc'],
            [STORE_TRADE_CRAFTERS]: ['companyProfileId', 'displayName'],
            [STORE_TRADE_ORDERS]: ['companyProfileId', 'status', 'commissionedAtUtc'],
            [STORE_TRADE_ORDER_CRAFT_SNAPSHOTS]: [
                'companyProfileId',
                'orderId',
                'updatedAtUtc'
            ],
            [STORE_TRADE_PAYROLL_DRAFTS]: [
                'companyProfileId',
                'orderId',
                'planSessionVersion',
                'updatedAtUtc'
            ],
            [STORE_COMPANY_RECORDS]: ['companyId', 'companyRevisionKey', 'recordKindKey'],
            [STORE_COMPANY_MUTATION_OUTBOX]: [
                'companyId',
                'companyStateKey',
                'createdAtUtc'
            ],
            [STORE_PORTABLE_SETTINGS]: ['scopeKey']
        }
    }[database.name] ?? {};
    const transactionStores = Object.keys(requiredIndexes);
    if (transactionStores.length === 0) {
        return null;
    }
    const transaction = database.transaction(transactionStores, 'readonly');
    const missingIndexes = [];
    for (const [storeName, indexNames] of Object.entries(requiredIndexes)) {
        const store = transaction.objectStore(storeName);
        for (const indexName of indexNames) {
            if (!store.indexNames.contains(indexName)) {
                missingIndexes.push(`${storeName}.${indexName}`);
            }
        }
    }
    return missingIndexes.length > 0
        ? `missing indexes ${missingIndexes.join(', ')}`
        : null;
}

function createMetadataStore(database) {
    if (!database.objectStoreNames.contains(STORE_STORAGE_METADATA)) {
        database.createObjectStore(STORE_STORAGE_METADATA, { keyPath: 'id' });
    }
}

function createPersonalSchema(database) {
    createMetadataStore(database);
    if (!database.objectStoreNames.contains(STORE_PLANS)) {
        const store = database.createObjectStore(STORE_PLANS, { keyPath: 'id' });
        store.createIndex('name', 'name', { unique: false });
        store.createIndex('modifiedAt', 'modifiedAt', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_PLAN_COMPONENTS)) {
        const store = database.createObjectStore(STORE_PLAN_COMPONENTS, { keyPath: 'id' });
        store.createIndex('planId', 'planId', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_PLAN_SUMMARIES)) {
        const store = database.createObjectStore(STORE_PLAN_SUMMARIES, { keyPath: 'id' });
        store.createIndex('name', 'name', { unique: false });
        store.createIndex('modifiedAt', 'modifiedAt', { unique: false });
        store.createIndex('savedAt', 'savedAt', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_SETTINGS)) {
        database.createObjectStore(STORE_SETTINGS, { keyPath: 'key' });
    }
}

function createMarketSchema(database) {
    createMetadataStore(database);
    if (!database.objectStoreNames.contains(STORE_MARKET_CACHE)) {
        const store = database.createObjectStore(STORE_MARKET_CACHE, { keyPath: 'key' });
        store.createIndex('fetchedAtUnix', 'fetchedAtUnix', { unique: false });
    }
}

function createLegacyTradeStores(database) {
    if (!database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES)) {
        const store = database.createObjectStore(STORE_TRADE_COMPANY_PROFILES, { keyPath: 'id' });
        store.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_TRADE_CRAFTERS)) {
        const store = database.createObjectStore(STORE_TRADE_CRAFTERS, { keyPath: 'id' });
        store.createIndex('companyProfileId', 'companyProfileId', { unique: false });
        store.createIndex('displayName', 'displayName', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_TRADE_ORDERS)) {
        const store = database.createObjectStore(STORE_TRADE_ORDERS, { keyPath: 'id' });
        store.createIndex('companyProfileId', 'companyProfileId', { unique: false });
        store.createIndex('status', 'status', { unique: false });
        store.createIndex('commissionedAtUtc', 'commissionedAtUtc', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS)) {
        const store = database.createObjectStore(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, { keyPath: 'id' });
        store.createIndex('companyProfileId', 'companyProfileId', { unique: false });
        store.createIndex('orderId', 'orderId', { unique: false });
        store.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS)) {
        const store = database.createObjectStore(STORE_TRADE_PAYROLL_DRAFTS, { keyPath: 'id' });
        store.createIndex('companyProfileId', 'companyProfileId', { unique: false });
        store.createIndex('orderId', 'orderId', { unique: false });
        store.createIndex('planSessionVersion', 'planSessionVersion', { unique: false });
        store.createIndex('updatedAtUtc', 'updatedAtUtc', { unique: false });
    }
}

function createCompanySchema(database) {
    createMetadataStore(database);
    createLegacyTradeStores(database);
    if (!database.objectStoreNames.contains(STORE_COMPANY_IDENTITIES)) {
        database.createObjectStore(STORE_COMPANY_IDENTITIES, { keyPath: 'companyId' });
    }
    if (!database.objectStoreNames.contains(STORE_COMPANY_RECORDS)) {
        const store = database.createObjectStore(STORE_COMPANY_RECORDS, { keyPath: 'key' });
        store.createIndex('companyId', 'companyId', { unique: false });
        store.createIndex('companyRevisionKey', 'companyRevisionKey', { unique: false });
        store.createIndex('recordKindKey', 'recordKindKey', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_COMPANY_MUTATION_OUTBOX)) {
        const store = database.createObjectStore(STORE_COMPANY_MUTATION_OUTBOX, { keyPath: 'key' });
        store.createIndex('companyId', 'companyId', { unique: false });
        store.createIndex('companyStateKey', 'companyStateKey', { unique: false });
        store.createIndex('createdAtUtc', 'createdAtUtc', { unique: false });
    }
    if (!database.objectStoreNames.contains(STORE_PORTABLE_SETTINGS)) {
        const store = database.createObjectStore(STORE_PORTABLE_SETTINGS, { keyPath: 'key' });
        store.createIndex('scopeKey', 'scopeKey', { unique: false });
    }
}

async function loadLegacySnapshot() {
    if (legacySnapshotInitialization) {
        return await legacySnapshotInitialization;
    }

    legacySnapshotInitialization = (async () => {
        if (!await databaseExists(LEGACY_DB_NAME)) {
            return Object.fromEntries(LEGACY_SNAPSHOT_STORES.map(name => [name, []]));
        }

        const legacy = await initLegacyDB();
        legacySnapshotSourceVersion = legacy.version;
        const availableStores = LEGACY_SNAPSHOT_STORES.filter(
            name => legacy.objectStoreNames.contains(name));
        const snapshot = Object.fromEntries(LEGACY_SNAPSHOT_STORES.map(name => [name, []]));
        if (availableStores.length === 0) {
            legacy.close();
            db = null;
            return snapshot;
        }

        try {
            await new Promise((resolve, reject) => {
                const transaction = legacy.transaction(availableStores, 'readonly');
                for (const storeName of availableStores) {
                    const request = transaction.objectStore(storeName).getAll();
                    request.onsuccess = () => {
                        snapshot[storeName] = request.result || [];
                    };
                    request.onerror = () => transaction.abort();
                }
                transaction.oncomplete = resolve;
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(
                    transaction.error ?? new Error('[IndexedDB] Legacy migration snapshot aborted.'));
            });
        } finally {
            legacy.close();
            db = null;
        }
        return snapshot;
    })();

    try {
        return await legacySnapshotInitialization;
    } catch (error) {
        legacySnapshotInitialization = null;
        legacySnapshotSourceVersion = null;
        throw error;
    }
}

async function loadMigrationMarker(database) {
    return await new Promise((resolve, reject) => {
        const request = database
            .transaction(STORE_STORAGE_METADATA, 'readonly')
            .objectStore(STORE_STORAGE_METADATA)
            .get(LEGACY_MIGRATION_ID);
        request.onsuccess = () => resolve(request.result ?? null);
        request.onerror = () => reject(request.error);
    });
}

function putMigrationMarker(transaction, domain, counts) {
    transaction.objectStore(STORE_STORAGE_METADATA).put({
        id: LEGACY_MIGRATION_ID,
        schemaVersion: 1,
        domain,
        sourceDatabase: legacySnapshotSourceVersion === null ? null : LEGACY_DB_NAME,
        sourceSchemaVersion: legacySnapshotSourceVersion,
        state: 'complete',
        counts,
        completedAtUtc: new Date().toISOString()
    });
}

async function migratePersonalDatabase(database) {
    if (await loadMigrationMarker(database)) {
        return;
    }

    const snapshot = await loadLegacySnapshot();
    const plans = snapshot[STORE_PLANS];
    const components = snapshot[STORE_PLAN_COMPONENTS];
    const summaries = snapshot[STORE_PLAN_SUMMARIES].length > 0
        ? snapshot[STORE_PLAN_SUMMARIES]
        : plans.map(toPlanSummary);
    const settings = snapshot[STORE_SETTINGS];
    const stores = [
        STORE_STORAGE_METADATA,
        STORE_PLANS,
        STORE_PLAN_COMPONENTS,
        STORE_PLAN_SUMMARIES,
        STORE_SETTINGS
    ];

    await new Promise((resolve, reject) => {
        const transaction = database.transaction(stores, 'readwrite');
        for (const record of plans) transaction.objectStore(STORE_PLANS).put(record);
        for (const record of components) transaction.objectStore(STORE_PLAN_COMPONENTS).put(record);
        for (const record of summaries) transaction.objectStore(STORE_PLAN_SUMMARIES).put(record);
        for (const record of settings) transaction.objectStore(STORE_SETTINGS).put(record);
        putMigrationMarker(transaction, 'personal', {
            plans: plans.length,
            planComponents: components.length,
            planSummaries: summaries.length,
            settings: settings.length
        });
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Personal storage migration aborted.'));
    });
}

async function migrateMarketDatabase(database) {
    if (await loadMigrationMarker(database)) {
        return;
    }

    const snapshot = await loadLegacySnapshot();
    const entries = snapshot[STORE_MARKET_CACHE].map(entry => {
        const fetchedAtUnix = getFetchedAtUnix(entry);
        return fetchedAtUnix > 0 ? { ...entry, fetchedAtUnix } : entry;
    });
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_STORAGE_METADATA, STORE_MARKET_CACHE],
            'readwrite');
        for (const record of entries) transaction.objectStore(STORE_MARKET_CACHE).put(record);
        putMigrationMarker(transaction, 'market', { marketCache: entries.length });
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Market storage migration aborted.'));
    });
}

function normalizeCompanyId(value) {
    const normalized = String(value || '').toLowerCase();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(normalized) ||
        normalized === '00000000-0000-0000-0000-000000000000') {
        throw new Error(`[IndexedDB] Invalid canonical company ID "${String(value || '')}".`);
    }
    return normalized;
}

function companyRecordKey(companyId, recordKind, recordId) {
    return `${normalizeCompanyId(companyId)}|${recordKind}|${String(recordId)}`;
}

function createCachedCompanyRecord(companyId, recordKind, recordId, payload, updatedAtUtc) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    if (!TRADE_COMPANY_RECORD_KINDS.includes(recordKind)) {
        throw new Error(`[IndexedDB] Unsupported company record kind "${recordKind}".`);
    }
    return {
        key: companyRecordKey(normalizedCompanyId, recordKind, recordId),
        companyId: normalizedCompanyId,
        recordKind,
        recordId: String(recordId),
        payloadJson: JSON.stringify(payload),
        recordRevision: 0,
        companyRevision: 0,
        companyRevisionKey: [normalizedCompanyId, 0],
        recordKindKey: [normalizedCompanyId, recordKind],
        updatedAtUtc: updatedAtUtc || new Date(0).toISOString(),
        deleted: false,
        deletedAtUtc: null,
        migrationState: 'legacy-local'
    };
}

async function migrateCompanyDatabase(database) {
    if (await loadMigrationMarker(database)) {
        return;
    }

    const snapshot = await loadLegacySnapshot();
    const profiles = snapshot[STORE_TRADE_COMPANY_PROFILES];
    const profileIds = new Set(profiles.map(profile => normalizeCompanyId(profile.id)));
    const canonicalIdentities = profiles.map(profile => ({
        companyId: normalizeCompanyId(profile.id),
        displayName: profile.name || 'Trade company',
        revision: 0,
        createdAtUtc: profile.createdAtUtc || new Date(0).toISOString(),
        updatedAtUtc: profile.updatedAtUtc || profile.createdAtUtc || new Date(0).toISOString(),
        migrationState: 'legacy-local'
    }));
    const canonicalRecords = [];
    for (const profile of profiles) {
        canonicalRecords.push(createCachedCompanyRecord(
            profile.id,
            'profile',
            profile.id,
            profile,
            profile.updatedAtUtc));
    }
    const legacyRecordSets = [
        [STORE_TRADE_CRAFTERS, 'crafter'],
        [STORE_TRADE_ORDERS, 'order'],
        [STORE_TRADE_ORDER_CRAFT_SNAPSHOTS, 'planArtifact'],
        [STORE_TRADE_PAYROLL_DRAFTS, 'payroll']
    ];
    for (const [storeName, recordKind] of legacyRecordSets) {
        for (const record of snapshot[storeName]) {
            const companyId = normalizeCompanyId(record.companyProfileId);
            if (!profileIds.has(companyId)) {
                throw new Error(
                    `[IndexedDB] ${storeName} record "${String(record.id)}" references an unknown company "${companyId}".`);
            }
            canonicalRecords.push(createCachedCompanyRecord(
                companyId,
                recordKind,
                record.id,
                record,
                record.updatedAtUtc));
        }
    }

    const stores = [
        STORE_STORAGE_METADATA,
        STORE_TRADE_COMPANY_PROFILES,
        STORE_TRADE_CRAFTERS,
        STORE_TRADE_ORDERS,
        STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
        STORE_TRADE_PAYROLL_DRAFTS,
        STORE_COMPANY_IDENTITIES,
        STORE_COMPANY_RECORDS
    ];
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(stores, 'readwrite');
        for (const storeName of [
            STORE_TRADE_COMPANY_PROFILES,
            STORE_TRADE_CRAFTERS,
            STORE_TRADE_ORDERS,
            STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
            STORE_TRADE_PAYROLL_DRAFTS
        ]) {
            for (const record of snapshot[storeName]) {
                transaction.objectStore(storeName).put(record);
            }
        }
        for (const identity of canonicalIdentities) {
            transaction.objectStore(STORE_COMPANY_IDENTITIES).put(identity);
        }
        for (const record of canonicalRecords) {
            transaction.objectStore(STORE_COMPANY_RECORDS).put(record);
        }
        putMigrationMarker(transaction, 'company', {
            tradeCompanyProfiles: profiles.length,
            tradeCrafters: snapshot[STORE_TRADE_CRAFTERS].length,
            tradeOrders: snapshot[STORE_TRADE_ORDERS].length,
            tradeOrderCraftSnapshots: snapshot[STORE_TRADE_ORDER_CRAFT_SNAPSHOTS].length,
            tradePayrollDrafts: snapshot[STORE_TRADE_PAYROLL_DRAFTS].length,
            canonicalRecords: canonicalRecords.length
        });
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Company storage migration aborted.'));
    });
}

async function initPersonalDatabase() {
    if (personalDb) return personalDb;
    personalInitialization ??= (async () => {
        const database = await openSpecializedDatabase(
            PERSONAL_DB_NAME,
            PERSONAL_DB_VERSION,
            createPersonalSchema);
        try {
            await migratePersonalDatabase(database);
            personalDb = database;
            return database;
        } catch (error) {
            database.close();
            throw error;
        }
    })();
    try {
        return await personalInitialization;
    } catch (error) {
        personalInitialization = null;
        throw error;
    }
}

async function initMarketDatabase() {
    if (marketDb) return marketDb;
    marketInitialization ??= (async () => {
        const database = await openSpecializedDatabase(
            MARKET_DB_NAME,
            MARKET_DB_VERSION,
            createMarketSchema);
        try {
            await migrateMarketDatabase(database);
            marketDb = database;
            return database;
        } catch (error) {
            database.close();
            throw error;
        }
    })();
    try {
        return await marketInitialization;
    } catch (error) {
        marketInitialization = null;
        throw error;
    }
}

async function initCompanyDatabase() {
    if (companyDb) return companyDb;
    companyInitialization ??= (async () => {
        const database = await openSpecializedDatabase(
            COMPANY_DB_NAME,
            COMPANY_DB_VERSION,
            createCompanySchema);
        try {
            await migrateCompanyDatabase(database);
            companyDb = database;
            return database;
        } catch (error) {
            database.close();
            throw error;
        }
    })();
    try {
        return await companyInitialization;
    } catch (error) {
        companyInitialization = null;
        throw error;
    }
}

async function initDB() {
    return await initPersonalDatabase();
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

function hasRequiredLegacyStores(database) {
    return database.objectStoreNames.contains(STORE_PLAN_COMPONENTS) &&
        database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES) &&
        database.objectStoreNames.contains(STORE_TRADE_CRAFTERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS) &&
        database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_MANIFESTS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_REVISIONS) &&
        database.objectStoreNames.contains(STORE_ENGINE_SESSION_COMPONENTS);
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
        const database = await initCompanyDatabase();
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
    const database = await initCompanyDatabase();
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
    const database = await initCompanyDatabase();
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
    const database = await initCompanyDatabase();
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
    const database = await initCompanyDatabase();
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
    const database = await initCompanyDatabase();
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
    const database = await initMarketDatabase();
    
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
    const database = await initMarketDatabase();
    
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
    const database = await initMarketDatabase();
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
    const database = await initMarketDatabase();
    
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
    const database = await initMarketDatabase();
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
    const database = await initMarketDatabase();
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
    const database = await initMarketDatabase();

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
    const database = await initMarketDatabase();
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
    const database = await initMarketDatabase();
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
    const database = await initMarketDatabase();

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

function readRevision(value, label) {
    const candidate = typeof value === 'object' && value !== null
        ? value.value ?? value.Value
        : value;
    if (!Number.isSafeInteger(candidate) || candidate < 0) {
        throw new Error(`[IndexedDB] ${label} must be a non-negative safe integer.`);
    }
    return candidate;
}

function readRequiredString(value, label) {
    const normalized = String(value ?? '').trim();
    if (!normalized) {
        throw new Error(`[IndexedDB] ${label} is required.`);
    }
    return normalized;
}

function normalizeMutationRequest(request) {
    const companyId = normalizeCompanyId(request?.companyId ?? request?.CompanyId);
    const recordKind = readRequiredString(
        request?.recordKind ?? request?.RecordKind,
        'Mutation record kind');
    if (!TRADE_COMPANY_RECORD_KINDS.includes(recordKind)) {
        throw new Error(`[IndexedDB] Unsupported company mutation kind "${recordKind}".`);
    }
    const protocolVersion = request?.protocolVersion ?? request?.ProtocolVersion;
    if (protocolVersion !== TRADE_COMPANY_PROTOCOL_VERSION) {
        throw new Error(
            `[IndexedDB] Company mutation protocol v${String(protocolVersion)} is incompatible; ` +
            `expected v${TRADE_COMPANY_PROTOCOL_VERSION}.`);
    }

    return {
        companyId,
        recordKind,
        recordId: readRequiredString(
            request?.recordId ?? request?.RecordId,
            'Mutation record ID'),
        payloadJson: readRequiredString(
            request?.payloadJson ?? request?.PayloadJson,
            'Mutation payload'),
        expectedRecordRevision: readRevision(
            request?.expectedRecordRevision ?? request?.ExpectedRecordRevision,
            'Expected company record revision'),
        expectedCompanyRevision: readRevision(
            request?.expectedCompanyRevision ?? request?.ExpectedCompanyRevision,
            'Expected company revision'),
        idempotencyKey: readRequiredString(
            request?.idempotencyKey ?? request?.IdempotencyKey,
            'Mutation idempotency key'),
        protocolVersion
    };
}

function normalizeCompanyIdentity(identity) {
    const companyId = normalizeCompanyId(identity?.companyId ?? identity?.CompanyId);
    return {
        companyId,
        displayName: readRequiredString(
            identity?.displayName ?? identity?.DisplayName,
            'Company display name'),
        revision: readRevision(identity?.revision ?? identity?.Revision, 'Company revision'),
        createdAtUtc: identity?.createdAtUtc ?? identity?.CreatedAtUtc,
        updatedAtUtc: identity?.updatedAtUtc ?? identity?.UpdatedAtUtc,
        migrationState: identity?.migrationState ?? null
    };
}

function normalizeCompanyRecord(record) {
    const companyId = normalizeCompanyId(record?.companyId ?? record?.CompanyId);
    const recordKind = readRequiredString(
        record?.recordKind ?? record?.RecordKind,
        'Company record kind');
    if (!TRADE_COMPANY_RECORD_KINDS.includes(recordKind)) {
        throw new Error(`[IndexedDB] Unsupported company record kind "${recordKind}".`);
    }
    const recordId = readRequiredString(record?.recordId ?? record?.RecordId, 'Company record ID');
    const recordRevision = readRevision(
        record?.recordRevision ?? record?.RecordRevision,
        'Company record revision');
    const companyRevision = readRevision(
        record?.companyRevision ?? record?.CompanyRevision,
        'Company revision');
    return {
        key: companyRecordKey(companyId, recordKind, recordId),
        companyId,
        recordKind,
        recordId,
        payloadJson: readRequiredString(record?.payloadJson ?? record?.PayloadJson, 'Company record payload'),
        recordRevision,
        companyRevision,
        companyRevisionKey: [companyId, companyRevision],
        recordKindKey: [companyId, recordKind],
        updatedAtUtc: record?.updatedAtUtc ?? record?.UpdatedAtUtc,
        deleted: Boolean(record?.deleted ?? record?.Deleted),
        deletedAtUtc: record?.deletedAtUtc ?? record?.DeletedAtUtc ?? null
    };
}

async function saveCachedTradeCompanyIdentity(identity) {
    const normalized = normalizeCompanyIdentity(identity);
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_COMPANY_IDENTITIES, 'readwrite');
        const store = transaction.objectStore(STORE_COMPANY_IDENTITIES);
        const request = store.get(normalized.companyId);
        request.onsuccess = () => {
            const current = request.result;
            if (current && readRevision(current.revision, 'Cached company revision') > normalized.revision) {
                transaction.abort();
                reject(new Error(
                    `[IndexedDB] Refusing to replace company ${normalized.companyId} revision ` +
                    `${current.revision} with stale revision ${normalized.revision}.`));
                return;
            }
            store.put(normalized);
        };
        request.onerror = () => transaction.abort();
        transaction.oncomplete = () => resolve(true);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Company identity cache write aborted.'));
    });
}

async function loadCachedTradeCompanyIdentity(companyId) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const request = database
            .transaction(STORE_COMPANY_IDENTITIES, 'readonly')
            .objectStore(STORE_COMPANY_IDENTITIES)
            .get(normalizedCompanyId);
        request.onsuccess = () => resolve(request.result ?? null);
        request.onerror = () => reject(request.error);
    });
}

async function applyTradeCompanyChangeSet(changeSet) {
    const companyId = normalizeCompanyId(changeSet?.companyId ?? changeSet?.CompanyId);
    const companyRevision = readRevision(
        changeSet?.companyRevision ?? changeSet?.CompanyRevision,
        'Change-set company revision');
    const records = (changeSet?.records ?? changeSet?.Records ?? []).map(normalizeCompanyRecord);
    if (records.some(record =>
        record.companyId !== companyId || record.companyRevision > companyRevision)) {
        throw new Error('[IndexedDB] Company change set contains a mismatched tenant or future revision.');
    }

    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_COMPANY_IDENTITIES, STORE_COMPANY_RECORDS],
            'readwrite');
        const identities = transaction.objectStore(STORE_COMPANY_IDENTITIES);
        const companyRecords = transaction.objectStore(STORE_COMPANY_RECORDS);
        const identityRequest = identities.get(companyId);
        identityRequest.onsuccess = () => {
            const identity = identityRequest.result;
            if (identity && readRevision(identity.revision, 'Cached company revision') > companyRevision) {
                transaction.abort();
                reject(new Error(
                    `[IndexedDB] Refusing stale company change set v${companyRevision}; ` +
                    `cached revision is v${identity.revision}.`));
                return;
            }
            if (identity) {
                identities.put({ ...identity, revision: companyRevision });
            }
            for (const record of records) {
                companyRecords.put(record);
            }
        };
        identityRequest.onerror = () => transaction.abort();
        transaction.oncomplete = () => resolve(true);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Company change-set cache write aborted.'));
    });
}

async function loadCachedTradeCompanyChanges(companyId, afterRevision) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const revision = readRevision(afterRevision, 'Cached changes cursor');
    const database = await initCompanyDatabase();
    const [identity, records] = await Promise.all([
        loadCachedTradeCompanyIdentity(normalizedCompanyId),
        new Promise((resolve, reject) => {
            const request = database
                .transaction(STORE_COMPANY_RECORDS, 'readonly')
                .objectStore(STORE_COMPANY_RECORDS)
                .index('companyId')
                .getAll(normalizedCompanyId);
            request.onsuccess = () => resolve(request.result || []);
            request.onerror = () => reject(request.error);
        })
    ]);
    if (!identity) {
        throw new Error(`[IndexedDB] Company ${normalizedCompanyId} is not present in the canonical cache.`);
    }
    return {
        companyId: normalizedCompanyId,
        companyRevision: identity.revision,
        records: records
            .filter(record => readRevision(record.companyRevision, 'Cached company revision') > revision)
            .sort((left, right) =>
                left.companyRevision - right.companyRevision ||
                left.key.localeCompare(right.key))
    };
}

async function loadCachedTradeCompanyRecord(companyId, recordKind, recordId) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const normalizedKind = readRequiredString(recordKind, 'Company record kind');
    const normalizedRecordId = readRequiredString(recordId, 'Company record ID');
    if (!TRADE_COMPANY_RECORD_KINDS.includes(normalizedKind)) {
        throw new Error(`[IndexedDB] Unsupported company record kind "${normalizedKind}".`);
    }
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const request = database
            .transaction(STORE_COMPANY_RECORDS, 'readonly')
            .objectStore(STORE_COMPANY_RECORDS)
            .get(companyRecordKey(normalizedCompanyId, normalizedKind, normalizedRecordId));
        request.onsuccess = () => resolve(request.result ?? null);
        request.onerror = () => reject(request.error);
    });
}

function createMutationOutboxEntry(request, state = 'pending') {
    return {
        key: `${request.companyId}|${request.idempotencyKey}`,
        companyId: request.companyId,
        companyStateKey: [request.companyId, state],
        state,
        request,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
        attemptCount: 0,
        result: null
    };
}

function putOutboxEntryIdempotently(transaction, normalizedRequest, onready = null) {
    const store = transaction.objectStore(STORE_COMPANY_MUTATION_OUTBOX);
    const candidate = createMutationOutboxEntry(normalizedRequest);
    const request = store.get(candidate.key);
    request.onsuccess = () => {
        const current = request.result;
        if (current &&
            JSON.stringify(current.request) !== JSON.stringify(normalizedRequest)) {
            transaction.abort();
            return;
        }
        if (!current) {
            store.put(candidate);
        }
        onready?.(current ?? candidate);
    };
    request.onerror = () => transaction.abort();
}

async function enqueueTradeCompanyMutation(request) {
    const normalized = normalizeMutationRequest(request);
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_COMPANY_MUTATION_OUTBOX, 'readwrite');
        let entry = null;
        putOutboxEntryIdempotently(transaction, normalized, value => {
            entry = value;
        });
        transaction.oncomplete = () => resolve(entry);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ??
            new Error('[IndexedDB] Mutation idempotency key was reused with different content.'));
    });
}

async function loadTradeCompanyMutationOutbox(companyId, includeTerminal = false) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const request = database
            .transaction(STORE_COMPANY_MUTATION_OUTBOX, 'readonly')
            .objectStore(STORE_COMPANY_MUTATION_OUTBOX)
            .index('companyId')
            .getAll(normalizedCompanyId);
        request.onsuccess = () => resolve((request.result || [])
            .filter(entry => includeTerminal || entry.state === 'pending')
            .sort((left, right) =>
                String(left.createdAtUtc).localeCompare(String(right.createdAtUtc)) ||
                left.key.localeCompare(right.key)));
        request.onerror = () => reject(request.error);
    });
}

async function markTradeCompanyMutationAttempt(companyId, idempotencyKey) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const normalizedIdempotencyKey = readRequiredString(
        idempotencyKey,
        'Mutation idempotency key');
    const key = `${normalizedCompanyId}|${normalizedIdempotencyKey}`;
    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE_COMPANY_MUTATION_OUTBOX, 'readwrite');
        const store = transaction.objectStore(STORE_COMPANY_MUTATION_OUTBOX);
        const request = store.get(key);
        let successor = null;
        request.onsuccess = () => {
            const entry = request.result;
            if (!entry || entry.state !== 'pending') {
                transaction.abort();
                return;
            }
            successor = {
                ...entry,
                attemptCount: (entry.attemptCount || 0) + 1,
                updatedAtUtc: new Date().toISOString()
            };
            store.put(successor);
        };
        request.onerror = () => transaction.abort();
        transaction.oncomplete = () => resolve(successor);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ??
            new Error(`[IndexedDB] Pending mutation outbox entry "${key}" does not exist.`));
    });
}

function mutationResultStatus(result) {
    const value = result?.status ?? result?.Status;
    if (typeof value === 'string') return value.toLowerCase();
    return ['applied', 'replayed', 'conflict', 'rejected'][value] ?? 'invalid';
}

async function completeTradeCompanyMutation(companyId, idempotencyKey, result) {
    const normalizedCompanyId = normalizeCompanyId(companyId);
    const normalizedIdempotencyKey = readRequiredString(idempotencyKey, 'Mutation idempotency key');
    const key = `${normalizedCompanyId}|${normalizedIdempotencyKey}`;
    const status = mutationResultStatus(result);
    if (!['applied', 'replayed', 'conflict', 'rejected'].includes(status)) {
        throw new Error(`[IndexedDB] Unsupported company mutation result "${status}".`);
    }

    const database = await initCompanyDatabase();
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_COMPANY_IDENTITIES, STORE_COMPANY_RECORDS, STORE_COMPANY_MUTATION_OUTBOX],
            'readwrite');
        const outbox = transaction.objectStore(STORE_COMPANY_MUTATION_OUTBOX);
        const request = outbox.get(key);
        request.onsuccess = () => {
            const entry = request.result;
            if (!entry) {
                transaction.abort();
                reject(new Error(`[IndexedDB] Mutation outbox entry "${key}" does not exist.`));
                return;
            }
            if (status === 'applied' || status === 'replayed') {
                const record = normalizeCompanyRecord(result?.record ?? result?.Record);
                if (record.companyId !== normalizedCompanyId) {
                    transaction.abort();
                    reject(new Error('[IndexedDB] Mutation result belongs to a different company.'));
                    return;
                }
                transaction.objectStore(STORE_COMPANY_RECORDS).put(record);
                const identities = transaction.objectStore(STORE_COMPANY_IDENTITIES);
                const identityRequest = identities.get(normalizedCompanyId);
                identityRequest.onsuccess = () => {
                    if (identityRequest.result) {
                        identities.put({
                            ...identityRequest.result,
                            revision: Math.max(
                                readRevision(identityRequest.result.revision, 'Cached company revision'),
                                record.companyRevision)
                        });
                    }
                    outbox.delete(key);
                };
                identityRequest.onerror = () => transaction.abort();
                return;
            }

            outbox.put({
                ...entry,
                state: status,
                companyStateKey: [normalizedCompanyId, status],
                updatedAtUtc: new Date().toISOString(),
                result
            });
        };
        request.onerror = () => transaction.abort();
        transaction.oncomplete = () => resolve(true);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Mutation completion transaction aborted.'));
    });
}

function portableSettingsScope(companyId, grantId) {
    const normalizedGrantId = String(grantId || '').toLowerCase();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(normalizedGrantId) ||
        normalizedGrantId === '00000000-0000-0000-0000-000000000000') {
        throw new Error('[IndexedDB] Portable settings require a non-empty operator grant ID.');
    }
    return `${normalizeCompanyId(companyId)}|${normalizedGrantId}`;
}

async function migratePortableOperatorSettings(companyId, grantId, portableKeys) {
    const scopeKey = portableSettingsScope(companyId, grantId);
    const keys = [...new Set(portableKeys || [])].sort();
    if (keys.some(key => typeof key !== 'string' || !key.trim())) {
        throw new Error('[IndexedDB] Portable setting keys must be non-empty strings.');
    }
    const personalSettings = await loadAllSettings();
    const database = await initCompanyDatabase();
    const markerId = `portable-settings:${scopeKey}:v${PORTABLE_SETTINGS_SCHEMA_VERSION}`;
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_STORAGE_METADATA, STORE_PORTABLE_SETTINGS],
            'readwrite');
        const metadata = transaction.objectStore(STORE_STORAGE_METADATA);
        const portable = transaction.objectStore(STORE_PORTABLE_SETTINGS);
        const markerRequest = metadata.get(markerId);
        markerRequest.onsuccess = () => {
            if (markerRequest.result?.state === 'complete') return;
            for (const key of keys) {
                if (!Object.prototype.hasOwnProperty.call(personalSettings, key)) continue;
                const portableKey = `${scopeKey}|${key}`;
                const request = portable.get(portableKey);
                request.onsuccess = () => {
                    if (!request.result) {
                        portable.put({
                            key: portableKey,
                            scopeKey,
                            companyId: scopeKey.split('|')[0],
                            grantId: scopeKey.split('|')[1],
                            settingKey: key,
                            value: personalSettings[key],
                            schemaVersion: PORTABLE_SETTINGS_SCHEMA_VERSION,
                            updatedAtUtc: new Date().toISOString()
                        });
                    }
                };
                request.onerror = () => transaction.abort();
            }
            metadata.put({
                id: markerId,
                schemaVersion: PORTABLE_SETTINGS_SCHEMA_VERSION,
                domain: 'portable-operator-settings',
                state: 'complete',
                sourceDatabase: PERSONAL_DB_NAME,
                completedAtUtc: new Date().toISOString()
            });
        };
        markerRequest.onerror = () => transaction.abort();
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ?? new Error('[IndexedDB] Portable settings migration aborted.'));
    });
    return await loadPortableOperatorSettings(companyId, grantId);
}

async function loadPortableOperatorSettings(companyId, grantId) {
    const scopeKey = portableSettingsScope(companyId, grantId);
    const database = await initCompanyDatabase();
    const records = await new Promise((resolve, reject) => {
        const request = database
            .transaction(STORE_PORTABLE_SETTINGS, 'readonly')
            .objectStore(STORE_PORTABLE_SETTINGS)
            .index('scopeKey')
            .getAll(scopeKey);
        request.onsuccess = () => resolve(request.result || []);
        request.onerror = () => reject(request.error);
    });
    return {
        schemaVersion: PORTABLE_SETTINGS_SCHEMA_VERSION,
        companyId: scopeKey.split('|')[0],
        grantId: scopeKey.split('|')[1],
        settings: Object.fromEntries(records
            .sort((left, right) => left.settingKey.localeCompare(right.settingKey))
            .map(record => [record.settingKey, record.value]))
    };
}

async function savePortableOperatorSettings(document, mutationRequest) {
    const companyId = normalizeCompanyId(document?.companyId ?? document?.CompanyId);
    const grantId = String(document?.grantId ?? document?.GrantId ?? '').toLowerCase();
    const scopeKey = portableSettingsScope(companyId, grantId);
    const schemaVersion = document?.schemaVersion ?? document?.SchemaVersion;
    if (schemaVersion !== PORTABLE_SETTINGS_SCHEMA_VERSION) {
        throw new Error(
            `[IndexedDB] Portable settings schema v${String(schemaVersion)} is incompatible; ` +
            `expected v${PORTABLE_SETTINGS_SCHEMA_VERSION}.`);
    }
    const settings = document?.settings ?? document?.Settings ?? {};
    const normalizedMutation = normalizeMutationRequest(mutationRequest);
    if (normalizedMutation.companyId !== companyId ||
        normalizedMutation.recordKind !== 'operatorSettings' ||
        normalizedMutation.recordId !== `operator:${grantId}`) {
        throw new Error('[IndexedDB] Portable settings mutation scope does not match the settings document.');
    }

    const database = await initCompanyDatabase();
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PORTABLE_SETTINGS, STORE_COMPANY_MUTATION_OUTBOX],
            'readwrite');
        const store = transaction.objectStore(STORE_PORTABLE_SETTINGS);
        const existingRequest = store.index('scopeKey').getAllKeys(scopeKey);
        existingRequest.onsuccess = () => {
            for (const key of existingRequest.result || []) store.delete(key);
            for (const [settingKey, value] of Object.entries(settings).sort(([left], [right]) =>
                left.localeCompare(right))) {
                store.put({
                    key: `${scopeKey}|${settingKey}`,
                    scopeKey,
                    companyId,
                    grantId,
                    settingKey,
                    value,
                    schemaVersion,
                    updatedAtUtc: new Date().toISOString()
                });
            }
            putOutboxEntryIdempotently(transaction, normalizedMutation);
        };
        existingRequest.onerror = () => transaction.abort();
        transaction.oncomplete = resolve;
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(
            transaction.error ??
            new Error('[IndexedDB] Portable settings and mutation outbox transaction aborted.'));
    });
    return true;
}

async function getSpecializedStorageDiagnostics() {
    const [personal, market, company, databases] = await Promise.all([
        initPersonalDatabase(),
        initMarketDatabase(),
        initCompanyDatabase(),
        indexedDB.databases()
    ]);
    const marker = async database => await loadMigrationMarker(database);
    return {
        databaseNames: {
            personal: PERSONAL_DB_NAME,
            market: MARKET_DB_NAME,
            engine: ENGINE_DB_NAME,
            company: COMPANY_DB_NAME
        },
        versions: {
            personal: personal.version,
            market: market.version,
            engine: ENGINE_DB_VERSION,
            company: company.version
        },
        engineDatabasePresent: databases.some(database => database.name === ENGINE_DB_NAME),
        migrations: {
            personal: await marker(personal),
            market: await marker(market),
            company: await marker(company)
        }
    };
}

// Export functions for Blazor interop
window.IndexedDB = {
    moduleRevision: MODULE_REVISION,
    schemaVersion: DB_VERSION,
    schemaVersions: Object.freeze({
        personal: PERSONAL_DB_VERSION,
        market: MARKET_DB_VERSION,
        engine: ENGINE_DB_VERSION,
        company: COMPANY_DB_VERSION
    }),
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
    getSpecializedStorageDiagnostics,
    saveCachedTradeCompanyIdentity,
    loadCachedTradeCompanyIdentity,
    applyTradeCompanyChangeSet,
    loadCachedTradeCompanyChanges,
    loadCachedTradeCompanyRecord,
    enqueueTradeCompanyMutation,
    loadTradeCompanyMutationOutbox,
    markTradeCompanyMutationAttempt,
    completeTradeCompanyMutation,
    migratePortableOperatorSettings,
    loadPortableOperatorSettings,
    savePortableOperatorSettings
};

console.log(
    `[IndexedDB] Module loaded (revision ${MODULE_REVISION}; ` +
    `personal ${PERSONAL_DB_VERSION}, market ${MARKET_DB_VERSION}, ` +
    `engine ${ENGINE_DB_VERSION}, company ${COMPANY_DB_VERSION})`);
