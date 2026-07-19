// IndexedDB module for FFXIV Craft Architect Web
// Uses Unix timestamps (seconds since epoch) for serialization safety

const DB_NAME = 'FFXIVCraftArchitect';
const DB_VERSION = 8;  // Adds durable Trade order craft snapshots
const STORE_PLANS = 'plans';
const STORE_PLAN_SUMMARIES = 'planSummaries';
const STORE_SETTINGS = 'settings';
const STORE_MARKET_CACHE = 'marketCache';
const STORE_TRADE_COMPANY_PROFILES = 'tradeCompanyProfiles';
const STORE_TRADE_CRAFTERS = 'tradeCrafters';
const STORE_TRADE_ORDERS = 'tradeOrders';
const STORE_TRADE_ORDER_CRAFT_SNAPSHOTS = 'tradeOrderCraftSnapshots';
const STORE_TRADE_PAYROLL_DRAFTS = 'tradePayrollDrafts';

let db = null;

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
                '[IndexedDB] Database opened successfully (v8 - Trade order craft snapshots)');
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
    return database.objectStoreNames.contains(STORE_TRADE_COMPANY_PROFILES) &&
        database.objectStoreNames.contains(STORE_TRADE_CRAFTERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDERS) &&
        database.objectStoreNames.contains(STORE_TRADE_ORDER_CRAFT_SNAPSHOTS) &&
        database.objectStoreNames.contains(STORE_TRADE_PAYROLL_DRAFTS);
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
    return {
        id: planData.id,
        name: planData.name || 'Saved Plan',
        modifiedAt: planData.modifiedAt,
        savedAt: planData.savedAt,
        dataCenter: planData.dataCenter || 'Aether',
        itemCount: Array.isArray(planData.projectItems) ? planData.projectItems.length : 0
    };
}

/**
 * Save a plan to IndexedDB
 */
async function savePlan(planData) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        
        const data = {
            ...planData,
            savedAt: planData.savedAt || new Date().toISOString(),
            modifiedAt: planData.modifiedAt || new Date().toISOString()
        };
        
        store.put(data);
        summaryStore.put(toPlanSummary(data));

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
        const transaction = database.transaction([STORE_PLANS], 'readonly');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.get(planId);
        
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
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
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        const request = store.get(planId);

        request.onsuccess = () => {
            const plan = request.result;
            if (!plan) {
                resolve(false);
                return;
            }

            const patched = {
                ...plan,
                marketPlansJson,
                marketIntelligenceJson,
                marketItemAnalysesJson,
                marketAnalysisRecipeBasisJson,
                marketAnalysisScopeSnapshotJson,
                savedRecommendationMode: recommendationMode,
                savedMarketAnalysisLens: marketAnalysisLens,
                modifiedAt: new Date().toISOString()
            };

            store.put(patched);
            summaryStore.put(toPlanSummary(patched));
        };

        request.onerror = () => reject(request.error);
        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

/**
 * Load all plans (sorted by modified date, newest first)
 */
async function loadAllPlans() {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS], 'readonly');
        const store = transaction.objectStore(STORE_PLANS);
        const index = store.index('modifiedAt');
        const request = index.openCursor(null, 'prev');
        
        const plans = [];
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                plans.push(cursor.value);
                cursor.continue();
            } else {
                resolve(plans);
            }
        };
        
        request.onerror = () => reject(request.error);
    });
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
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        store.delete(planId);
        summaryStore.delete(planId);

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
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        for (const plan of plans || []) {
            store.put(plan);
            summaryStore.put(toPlanSummary(plan));
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
        const transaction = database.transaction([STORE_PLANS, STORE_PLAN_SUMMARIES], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const summaryStore = transaction.objectStore(STORE_PLAN_SUMMARIES);
        store.clear();
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
    
    console.log('[IndexedDB] Deleting stale entries older than Unix timestamp:', cutoffUnix);
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.openCursor();
        
        let deletedCount = 0;
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                const entry = cursor.value;
                const entryUnix = getFetchedAtUnix(entry);
                
                // Check if this entry is stale (older than cutoff)
                if (entryUnix <= cutoffUnix) {
                    store.delete(cursor.primaryKey);
                    deletedCount++;
                }
                cursor.continue();
            } else {
                if (deletedCount > 0) {
                    console.log('[IndexedDB] Deleted', deletedCount, 'stale entries');
                }
                resolve(deletedCount);
            }
        };
        
        request.onerror = () => {
            console.error('[IndexedDB] Failed to delete stale entries:', request.error);
            reject(request.error);
        };
    });
}

/**
 * Delete oldest N entries (LRU eviction)
 * @param {number} count - Number of entries to delete
 */
async function deleteOldestEntries(count) {
    const database = await initDB();
    
    console.log('[IndexedDB] Deleting', count, 'oldest entries');
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        
        // Can't use index cursor since we need to handle both old/new formats
        // Collect all entries, sort by timestamp, delete oldest
        const request = store.openCursor();
        const entries = [];
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                entries.push({
                    key: cursor.primaryKey,
                    unix: getFetchedAtUnix(cursor.value)
                });
                cursor.continue();
            } else {
                // Sort by timestamp (oldest first)
                entries.sort((a, b) => a.unix - b.unix);
                
                // Delete oldest N
                let deletedCount = 0;
                for (let i = 0; i < Math.min(count, entries.length); i++) {
                    store.delete(entries[i].key);
                    deletedCount++;
                }
                console.log('[IndexedDB] Deleted', deletedCount, 'oldest entries');
                resolve(deletedCount);
            }
        };
        
        request.onerror = () => {
            console.error('[IndexedDB] Failed to delete oldest entries:', request.error);
            reject(request.error);
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
        const request = store.openCursor();
        
        let total = 0;
        let valid = 0;
        let stale = 0;
        let oldestUnix = null;
        let newestUnix = null;
        let totalSize = 0;
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                const entry = cursor.value;
                const entryUnix = getFetchedAtUnix(entry);
                
                total++;
                totalSize += JSON.stringify(entry).length * 2; // Rough byte estimate
                
                // Track oldest/newest
                if (oldestUnix === null || entryUnix < oldestUnix) {
                    oldestUnix = entryUnix;
                }
                if (newestUnix === null || entryUnix > newestUnix) {
                    newestUnix = entryUnix;
                }
                
                // Check staleness using Unix timestamp comparison
                if (entryUnix <= cutoffUnix) {
                    stale++;
                } else {
                    valid++;
                }
                
                cursor.continue();
            } else {
                const stats = {
                    total,
                    valid,
                    stale,
                    oldestUnix: oldestUnix || 0,
                    newestUnix: newestUnix || 0,
                    sizeBytes: totalSize
                };
                console.log('[IndexedDB] Cache stats:', stats);
                resolve(stats);
            }
        };
        
        request.onerror = () => {
            console.error('[IndexedDB] Failed to get stats:', request.error);
            reject(request.error);
        };
    });
}

// Export functions for Blazor interop
window.IndexedDB = {
    savePlan,
    loadPlan,
    loadAllPlans,
    loadPlanSummaries,
    savePlansBatch,
    patchMarketAnalysis,
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
    getTradeStoreDiagnostics
};

console.log('[IndexedDB] Module loaded (v9 with market data freshness probe)');
