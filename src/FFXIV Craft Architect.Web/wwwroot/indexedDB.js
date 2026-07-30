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
const MODULE_REVISION = 24;
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
const LEGACY_MIGRATION_ID = 'legacy-monolith-v15';
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
            STORE_TRADE_PAYROLL_DRAFTS
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
            ]
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

async function migrateCompanyDatabase(database) {
    if (await loadMigrationMarker(database)) {
        return;
    }

    const snapshot = await loadLegacySnapshot();
    const stores = [
        STORE_STORAGE_METADATA,
        STORE_TRADE_COMPANY_PROFILES,
        STORE_TRADE_CRAFTERS,
        STORE_TRADE_ORDERS,
        STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
        STORE_TRADE_PAYROLL_DRAFTS
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
        putMigrationMarker(transaction, 'company', {
            tradeCompanyProfiles: snapshot[STORE_TRADE_COMPANY_PROFILES].length,
            tradeCrafters: snapshot[STORE_TRADE_CRAFTERS].length,
            tradeOrders: snapshot[STORE_TRADE_ORDERS].length,
            tradeOrderCraftSnapshots: snapshot[STORE_TRADE_ORDER_CRAFT_SNAPSHOTS].length,
            tradePayrollDrafts: snapshot[STORE_TRADE_PAYROLL_DRAFTS].length
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

async function deleteTradeCompanyProfile(companyProfileId) {
    return await deleteStoreRecord(STORE_TRADE_COMPANY_PROFILES, companyProfileId);
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

async function deleteTradeCrafter(crafterId) {
    return await deleteStoreRecord(STORE_TRADE_CRAFTERS, crafterId);
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

async function openExistingDatabaseReadOnly(name) {
    if (typeof indexedDB.databases !== 'function') {
        throw new Error('[IndexedDB] This browser cannot enumerate databases for read-only inventory.');
    }
    const databases = await indexedDB.databases();
    if (!databases.some(database => database.name === name)) {
        return null;
    }

    return await new Promise((resolve, reject) => {
        let disappeared = false;
        const request = indexedDB.open(name);
        request.onupgradeneeded = event => {
            disappeared = true;
            event.target.transaction.abort();
        };
        request.onerror = () => reject(disappeared
            ? new Error(`[IndexedDB] ${name} disappeared during read-only inventory.`)
            : request.error);
        request.onsuccess = () => {
            request.result.onversionchange = () => request.result.close();
            resolve(request.result);
        };
    });
}

async function readExistingStoreRecords(database, storeName) {
    if (!database?.objectStoreNames.contains(storeName)) {
        return [];
    }
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly');
        const request = transaction.objectStore(storeName).getAll();
        request.onsuccess = () => resolve(request.result || []);
        request.onerror = () => reject(request.error);
    });
}

async function materializeExistingPlan(database, record) {
    if (!record || !isComponentStoredPlan(record)) {
        return record || null;
    }
    if (!database?.objectStoreNames.contains(STORE_PLANS) ||
        !database.objectStoreNames.contains(STORE_PLAN_COMPONENTS)) {
        throw new Error('Saved plan component stores are unavailable.');
    }
    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_PLANS, STORE_PLAN_COMPONENTS],
            'readonly');
        let materialized = null;
        materializeStoredPlanRecord(transaction, record, value => {
            materialized = value;
        });
        transaction.oncomplete = () => resolve(materialized);
        transaction.onerror = event => reject(transaction.error || event.target?.error);
        transaction.onabort = event => reject(
            transaction.error || event.target?.error || new Error('Saved plan is incomplete.'));
    });
}

async function getCompanyMigrationSourceInventory() {
    const [companyDatabase, personalDatabase, legacyDatabase] = await Promise.all([
        openExistingDatabaseReadOnly(COMPANY_DB_NAME),
        openExistingDatabaseReadOnly(PERSONAL_DB_NAME),
        openExistingDatabaseReadOnly(LEGACY_DB_NAME)
    ]);
    try {
        for (const [database, name, supportedVersion] of [
            [companyDatabase, COMPANY_DB_NAME, COMPANY_DB_VERSION],
            [personalDatabase, PERSONAL_DB_NAME, PERSONAL_DB_VERSION],
            [legacyDatabase, LEGACY_DB_NAME, LEGACY_DB_VERSION]
        ]) {
            if (database && database.version > supportedVersion) {
                throw new Error(
                    `[IndexedDB] ${name} schema v${database.version} exceeds ` +
                    `the supported read-only inventory schema v${supportedVersion}.`);
            }
        }
        const companyStoreNames = companyDatabase
            ? Array.from(companyDatabase.objectStoreNames)
            : [];
        const legacyStoreNames = legacyDatabase
            ? Array.from(legacyDatabase.objectStoreNames)
            : [];
        const knownCompanyStores = [
            STORE_STORAGE_METADATA,
            STORE_TRADE_COMPANY_PROFILES,
            STORE_TRADE_CRAFTERS,
            STORE_TRADE_ORDERS,
            STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
            STORE_TRADE_PAYROLL_DRAFTS
        ];
        const legacyInventoryStores = [
            STORE_PLANS,
            STORE_PLAN_COMPONENTS,
            STORE_TRADE_COMPANY_PROFILES,
            STORE_TRADE_CRAFTERS,
            STORE_TRADE_ORDERS,
            STORE_TRADE_ORDER_CRAFT_SNAPSHOTS,
            STORE_TRADE_PAYROLL_DRAFTS
        ];
        const knownLegacyStores = new Set([
            ...LEGACY_SNAPSHOT_STORES,
            STORE_ENGINE_SESSION_MANIFESTS,
            STORE_ENGINE_SESSION_REVISIONS,
            STORE_ENGINE_SESSION_COMPONENTS
        ]);
        const unsupportedLegacyStoreNames = legacyStoreNames
            .filter(storeName => !knownLegacyStores.has(storeName))
            .sort((left, right) => left.localeCompare(right));
        const legacyReadStoreNames = Array.from(new Set([
            ...legacyInventoryStores,
            ...unsupportedLegacyStoreNames
        ]));
        const companyRecords = Object.fromEntries(await Promise.all(
            companyStoreNames.map(async storeName => [
                storeName,
                await readExistingStoreRecords(companyDatabase, storeName)
            ])));
        const personalPlans = await readExistingStoreRecords(personalDatabase, STORE_PLANS);
        const personalComponents = await readExistingStoreRecords(
            personalDatabase,
            STORE_PLAN_COMPONENTS);
        const personalMarkers = await readExistingStoreRecords(
            personalDatabase,
            STORE_STORAGE_METADATA);
        const legacyRecords = Object.fromEntries(await Promise.all(
            legacyReadStoreNames.map(async storeName => [
                storeName,
                await readExistingStoreRecords(legacyDatabase, storeName)
            ])));
        const companyOrders = companyRecords[STORE_TRADE_ORDERS] || [];
        const legacyOrders = legacyRecords[STORE_TRADE_ORDERS] || [];
        const personalRequiredIds = new Set(companyOrders
            .map(order => order?.craftPlanId)
            .filter(planId => typeof planId === 'string' && planId.length > 0));
        const legacyRequiredIds = new Set(legacyOrders
            .map(order => order?.craftPlanId)
            .filter(planId => typeof planId === 'string' && planId.length > 0));
        const linkedPlanIds = Array.from(new Set([
            ...personalRequiredIds,
            ...legacyRequiredIds
        ])).sort((left, right) => left.localeCompare(right));
        const materializeCandidates = async (
            databaseRole,
            database,
            plans,
            requiredIds) => await Promise.all(linkedPlanIds.flatMap(planId => {
                const record = plans.find(plan => plan?.id === planId);
                if (!record && !requiredIds.has(planId)) {
                    return [];
                }
                return [(async () => {
                    try {
                        return {
                            databaseRole,
                            planId,
                            requiredBySource: requiredIds.has(planId),
                            payload: record
                                ? await materializeExistingPlan(database, record)
                                : null,
                            error: record ? null : 'Saved plan is missing from this source database.'
                        };
                    } catch (error) {
                        return {
                            databaseRole,
                            planId,
                            requiredBySource: requiredIds.has(planId),
                            payload: null,
                            error: formatIndexedDbError(error)
                        };
                    }
                })()];
            }));
        const linkedPlans = [
            ...await materializeCandidates(
                'personal',
                personalDatabase,
                personalPlans,
                personalRequiredIds),
            ...await materializeCandidates(
                'legacy',
                legacyDatabase,
                legacyRecords[STORE_PLANS] || [],
                legacyRequiredIds)
        ];
        const linkedPlanIdSet = new Set(linkedPlanIds);
        const relevantPlans = plans => plans.filter(plan => linkedPlanIdSet.has(plan?.id));
        const componentIds = plans => new Set(relevantPlans(plans).flatMap(plan =>
            Object.values(plan?.componentRefs || {})
                .filter(componentId => typeof componentId === 'string')));
        const relevantComponents = (plans, components) => {
            const ids = componentIds(plans);
            return components.filter(component =>
                ids.has(component?.id) || linkedPlanIdSet.has(component?.planId));
        };
        const databaseState = database => ({
            exists: Boolean(database),
            schemaVersion: database?.version ?? null,
            storeNames: database ? Array.from(database.objectStoreNames) : []
        });

        return {
            formatVersion: 2,
            capturedAtUtc: new Date().toISOString(),
            origin: globalThis.location?.origin || null,
            moduleRevision: MODULE_REVISION,
            specializedStorage: {
                readOnlyCapture: true,
                databaseNames: {
                    personal: PERSONAL_DB_NAME,
                    company: COMPANY_DB_NAME,
                    legacy: LEGACY_DB_NAME
                },
                databases: {
                    personal: databaseState(personalDatabase),
                    company: databaseState(companyDatabase),
                    legacy: databaseState(legacyDatabase)
                },
                migrations: {
                    personal: personalMarkers.find(marker => marker?.id === LEGACY_MIGRATION_ID) || null,
                    company: (companyRecords[STORE_STORAGE_METADATA] || [])
                        .find(marker => marker?.id === LEGACY_MIGRATION_ID) || null
                }
            },
            company: {
                databaseName: COMPANY_DB_NAME,
                exists: Boolean(companyDatabase),
                schemaVersion: companyDatabase?.version ?? null,
                companyProfiles: companyRecords[STORE_TRADE_COMPANY_PROFILES] || [],
                crafters: companyRecords[STORE_TRADE_CRAFTERS] || [],
                orders: companyOrders,
                orderCraftSnapshots:
                    companyRecords[STORE_TRADE_ORDER_CRAFT_SNAPSHOTS] || [],
                payrollDrafts: companyRecords[STORE_TRADE_PAYROLL_DRAFTS] || [],
                unsupportedStores: companyStoreNames
                    .filter(storeName => !knownCompanyStores.includes(storeName))
                    .sort((left, right) => left.localeCompare(right))
                    .map(storeName => ({
                        storeName,
                        records: companyRecords[storeName]
                    }))
            },
            personal: {
                databaseName: PERSONAL_DB_NAME,
                exists: Boolean(personalDatabase),
                schemaVersion: personalDatabase?.version ?? null,
                linkedPlans: relevantPlans(personalPlans),
                linkedPlanComponents: relevantComponents(personalPlans, personalComponents)
            },
            linkedPlans,
            legacy: {
                databaseName: LEGACY_DB_NAME,
                exists: Boolean(legacyDatabase),
                schemaVersion: legacyDatabase?.version ?? null,
                companyProfiles: legacyRecords[STORE_TRADE_COMPANY_PROFILES] || [],
                crafters: legacyRecords[STORE_TRADE_CRAFTERS] || [],
                orders: legacyOrders,
                orderCraftSnapshots:
                    legacyRecords[STORE_TRADE_ORDER_CRAFT_SNAPSHOTS] || [],
                payrollDrafts: legacyRecords[STORE_TRADE_PAYROLL_DRAFTS] || [],
                linkedPlans: relevantPlans(legacyRecords[STORE_PLANS] || []),
                linkedPlanComponents: relevantComponents(
                    legacyRecords[STORE_PLANS] || [],
                    legacyRecords[STORE_PLAN_COMPONENTS] || []),
                unsupportedStores: unsupportedLegacyStoreNames.map(storeName => ({
                    storeName,
                    records: legacyRecords[storeName]
                }))
            }
        };
    } finally {
        companyDatabase?.close();
        personalDatabase?.close();
        legacyDatabase?.close();
    }
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
    deleteTradeCompanyProfile,
    saveTradeCrafter,
    saveTradeCraftersBatch,
    loadTradeCrafters,
    deleteTradeCrafter,
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
    getCompanyMigrationSourceInventory
};

console.log(
    `[IndexedDB] Module loaded (revision ${MODULE_REVISION}; ` +
    `personal ${PERSONAL_DB_VERSION}, market ${MARKET_DB_VERSION}, ` +
    `engine ${ENGINE_DB_VERSION}, company ${COMPANY_DB_VERSION})`);
