// IndexedDB module for FFXIV Craft Architect Web
// Uses Unix timestamps (seconds since epoch) for serialization safety

const DB_NAME = 'FFXIVCraftArchitect';
const DB_VERSION = 5;  // Bumped for market-intelligence cold storage
const STORE_PLANS = 'plans';
const STORE_PLAN_SUMMARIES = 'planSummaries';
const STORE_SETTINGS = 'settings';
const STORE_MARKET_CACHE = 'marketCache';
const STORE_MARKET_PUBLICATIONS = 'marketPublications';
const STORE_MARKET_LISTING_DETAILS = 'marketListingDetails';
const STORE_MARKET_FETCHES = 'marketFetches';
const STORE_MARKET_ANALYSIS_RUNS = 'marketAnalysisRuns';

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
            resolve(attachDatabaseConnection(
                request.result,
                '[IndexedDB] Database opened successfully (v5 - market intelligence)'));
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

            if (!database.objectStoreNames.contains(STORE_MARKET_PUBLICATIONS)) {
                const publicationStore = database.createObjectStore(STORE_MARKET_PUBLICATIONS, { keyPath: 'publicationId' });
                publicationStore.createIndex('publishedAtUtc', 'publicationContext.publishedAtUtc', { unique: false });
            }

            if (!database.objectStoreNames.contains(STORE_MARKET_LISTING_DETAILS)) {
                const detailStore = database.createObjectStore(STORE_MARKET_LISTING_DETAILS, { keyPath: 'storageKey' });
                detailStore.createIndex('publicationId', 'publicationId', { unique: false });
                detailStore.createIndex('itemId', 'itemId', { unique: false });
            }

            if (!database.objectStoreNames.contains(STORE_MARKET_FETCHES)) {
                const fetchStore = database.createObjectStore(STORE_MARKET_FETCHES, { keyPath: 'storageKey' });
                fetchStore.createIndex('publicationId', 'publicationId', { unique: false });
                fetchStore.createIndex('runId', 'runId', { unique: false });
                fetchStore.createIndex('itemId', 'itemId', { unique: false });
            }

            if (!database.objectStoreNames.contains(STORE_MARKET_ANALYSIS_RUNS)) {
                const runStore = database.createObjectStore(STORE_MARKET_ANALYSIS_RUNS, { keyPath: 'runId' });
                runStore.createIndex('publicationId', 'publicationId', { unique: false });
            }

        };
    });
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
    marketAnalysisScopeSnapshotJson,
    activeMarketIntelligencePublicationId,
    marketIntelligenceSummaryJson) {
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
                activeMarketIntelligencePublicationId,
                marketIntelligenceSummaryJson,
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

function getField(value, camelName, pascalName) {
    if (!value) {
        return undefined;
    }

    return value[camelName] ?? value[pascalName];
}

function getFingerprintValue(value) {
    if (!value) {
        return '';
    }

    return typeof value === 'string'
        ? value
        : getField(value, 'value', 'Value') ?? '';
}

function normalizeGuid(value) {
    return value ? String(value).toLowerCase() : '';
}

function marketWorldKeyPart(world) {
    if (!world) {
        return 'none';
    }

    const dataCenter = getField(world, 'dataCenter', 'DataCenter') ?? '';
    const worldName = getField(world, 'worldName', 'WorldName') ?? '';
    return `${dataCenter}|${worldName}`;
}

function marketDetailStorageKey(key) {
    const publicationId = normalizeGuid(getField(key, 'publicationId', 'PublicationId'));
    const scope = getField(key, 'scope', 'Scope') ?? 0;
    const itemId = getField(key, 'itemId', 'ItemId') ?? 0;
    const world = marketWorldKeyPart(getField(key, 'world', 'World'));
    const demandFingerprint = getFingerprintValue(getField(key, 'demandFingerprint', 'DemandFingerprint'));
    return `${publicationId}|${scope}|${itemId}|${world}|${demandFingerprint}`;
}

function marketListingFactStorageKey(fact, index) {
    const publicationId = normalizeGuid(getField(fact, 'publicationId', 'PublicationId'));
    const runId = normalizeGuid(getField(fact, 'runId', 'RunId'));
    const scope = getField(fact, 'scope', 'Scope') ?? 0;
    const itemId = getField(fact, 'itemId', 'ItemId') ?? 0;
    const dataCenter = getField(fact, 'dataCenter', 'DataCenter') ?? '';
    const worldName = getField(fact, 'worldName', 'WorldName') ?? '';
    const demandFingerprint = getFingerprintValue(getField(fact, 'demandFingerprint', 'DemandFingerprint'));
    const listingId = getField(fact, 'listingId', 'ListingId');
    if (listingId) {
        const dataCenter = getField(fact, 'dataCenter', 'DataCenter') ?? '';
        const worldId = getField(fact, 'worldId', 'WorldId') ?? '';
        const retrievedAt = getField(fact, 'retrievedAtUtc', 'RetrievedAtUtc') ?? '';
        return `${publicationId}|${runId}|${scope}|${itemId}|${dataCenter}|${worldId}|${worldName}|${demandFingerprint}|${retrievedAt}|id:${listingId}`;
    }

    const retainer = getField(fact, 'retainerName', 'RetainerName') ?? '';
    const unitPrice = getField(fact, 'unitPrice', 'UnitPrice') ?? 0;
    const quantity = getField(fact, 'quantity', 'Quantity') ?? 0;
    const isHq = getField(fact, 'isHq', 'IsHq') ? 'hq' : 'nq';
    const retrievedAt = getField(fact, 'retrievedAtUtc', 'RetrievedAtUtc') ?? '';
    return `${publicationId}|${runId}|${scope}|${itemId}|${dataCenter}|${worldName}|${demandFingerprint}|${retainer}|${unitPrice}|${quantity}|${isHq}|${retrievedAt}|${index}`;
}

function normalizeDetailForStorage(detail) {
    const key = getField(detail, 'key', 'Key');
    return {
        ...detail,
        storageKey: marketDetailStorageKey(key),
        publicationId: normalizeGuid(getField(key, 'publicationId', 'PublicationId')),
        itemId: getField(key, 'itemId', 'ItemId') ?? 0
    };
}

function normalizeFactForStorage(fact, index) {
    return {
        ...fact,
        storageKey: marketListingFactStorageKey(fact, index),
        publicationId: normalizeGuid(getField(fact, 'publicationId', 'PublicationId')),
        runId: normalizeGuid(getField(fact, 'runId', 'RunId')),
        itemId: getField(fact, 'itemId', 'ItemId') ?? 0
    };
}

async function saveMarketPublication(publicationWrite) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_MARKET_PUBLICATIONS, STORE_MARKET_LISTING_DETAILS, STORE_MARKET_ANALYSIS_RUNS],
            'readwrite');
        const publicationStore = transaction.objectStore(STORE_MARKET_PUBLICATIONS);
        const detailStore = transaction.objectStore(STORE_MARKET_LISTING_DETAILS);
        const runStore = transaction.objectStore(STORE_MARKET_ANALYSIS_RUNS);

        const summary = getField(publicationWrite, 'summary', 'Summary');
        const details = getField(publicationWrite, 'details', 'Details') ?? [];
        const runRecords = getField(publicationWrite, 'runRecords', 'RunRecords') ?? [];

        for (const detail of details) {
            detailStore.put(normalizeDetailForStorage(detail));
        }

        for (const runRecord of runRecords) {
            runStore.put(runRecord);
        }

        publicationStore.put(summary);

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function saveMarketPublicationDetails(publicationId, details) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_LISTING_DETAILS], 'readwrite');
        const detailStore = transaction.objectStore(STORE_MARKET_LISTING_DETAILS);

        for (const detail of details ?? []) {
            detailStore.put(normalizeDetailForStorage(detail));
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function saveMarketPublicationRunRecords(publicationId, runRecords) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_ANALYSIS_RUNS], 'readwrite');
        const runStore = transaction.objectStore(STORE_MARKET_ANALYSIS_RUNS);

        for (const runRecord of runRecords ?? []) {
            runStore.put(runRecord);
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function saveMarketPublicationSummary(summary) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_PUBLICATIONS], 'readwrite');
        const publicationStore = transaction.objectStore(STORE_MARKET_PUBLICATIONS);

        publicationStore.put(summary);

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

async function loadMarketPublicationSummary(publicationId) {
    const database = await initDB();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_PUBLICATIONS], 'readonly');
        const request = transaction.objectStore(STORE_MARKET_PUBLICATIONS).get(publicationId);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
    });
}

async function loadMarketDetailManifest(publicationId) {
    const summary = await loadMarketPublicationSummary(publicationId);
    return getField(summary, 'detailManifest', 'DetailManifest') ?? null;
}

async function loadMarketDetails(query) {
    const database = await initDB();
    const publicationId = normalizeGuid(getField(query, 'publicationId', 'PublicationId'));
    const itemId = getField(query, 'itemId', 'ItemId');
    const world = getField(query, 'world', 'World');
    const worldKey = world ? marketWorldKeyPart(world) : null;
    const demandFingerprint = getFingerprintValue(getField(query, 'demandFingerprint', 'DemandFingerprint'));

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_LISTING_DETAILS], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_LISTING_DETAILS);
        const index = store.index('publicationId');
        const request = index.openCursor(publicationId);
        const results = [];

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor) {
                resolve(results);
                return;
            }

            const detail = cursor.value;
            const key = getField(detail, 'key', 'Key');
            const keyWorld = getField(key, 'world', 'World');
            if ((itemId == null || getField(key, 'itemId', 'ItemId') === itemId) &&
                (worldKey == null || marketWorldKeyPart(keyWorld) === worldKey) &&
                (!demandFingerprint ||
                    getFingerprintValue(getField(key, 'demandFingerprint', 'DemandFingerprint')) === demandFingerprint)) {
                results.push(detail);
            }

            cursor.continue();
        };

        request.onerror = () => reject(request.error);
    });
}

async function loadMarketRunRecord(runId) {
    const database = await initDB();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_ANALYSIS_RUNS], 'readonly');
        const request = transaction.objectStore(STORE_MARKET_ANALYSIS_RUNS).get(runId);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
    });
}

async function pruneMarketDetails(pruneRequest) {
    const database = await initDB();
    const activePublicationId = normalizeGuid(getField(pruneRequest, 'keepActivePublicationId', 'KeepActivePublicationId'));
    const pruneOlderThanRaw = getField(pruneRequest, 'pruneDetailsOlderThanUtc', 'PruneDetailsOlderThanUtc');
    const pruneOlderThanTime = pruneOlderThanRaw ? new Date(pruneOlderThanRaw).getTime() : null;
    const keepRecentPublicationCount = getField(pruneRequest, 'keepRecentPublicationCount', 'KeepRecentPublicationCount');

    if (!activePublicationId && !pruneOlderThanTime && !keepRecentPublicationCount) {
        return true;
    }

    return new Promise((resolve, reject) => {
        const transaction = database.transaction(
            [STORE_MARKET_PUBLICATIONS, STORE_MARKET_LISTING_DETAILS],
            'readwrite');
        const publicationStore = transaction.objectStore(STORE_MARKET_PUBLICATIONS);
        const detailStore = transaction.objectStore(STORE_MARKET_LISTING_DETAILS);
        const publicationRequest = publicationStore.openCursor();
        const summaries = [];

        publicationRequest.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor) {
                const keepPublicationIds = buildMarketPublicationRetentionSet(
                    summaries,
                    activePublicationId,
                    pruneOlderThanTime,
                    keepRecentPublicationCount);

                const detailRequest = detailStore.openCursor();
                detailRequest.onsuccess = (detailEvent) => {
                    const detailCursor = detailEvent.target.result;
                    if (!detailCursor) {
                        return;
                    }

                    if (!keepPublicationIds.has(normalizeGuid(detailCursor.value.publicationId))) {
                        detailCursor.delete();
                    }

                    detailCursor.continue();
                };

                for (const summary of summaries) {
                    const publicationId = normalizeGuid(getField(summary, 'publicationId', 'PublicationId'));
                    if (!keepPublicationIds.has(publicationId)) {
                        markMarketPublicationDetailsPruned(summary);
                        publicationStore.put(summary);
                    }
                }
                return;
            }

            summaries.push(cursor.value);
            cursor.continue();
        };

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

function buildMarketPublicationRetentionSet(
    summaries,
    activePublicationId,
    pruneOlderThanTime,
    keepRecentPublicationCount) {
    const keep = new Set();
    if (activePublicationId) {
        keep.add(activePublicationId);
    }

    if (pruneOlderThanTime) {
        for (const summary of summaries) {
            const publishedTime = getMarketPublicationPublishedTime(summary);
            if (publishedTime >= pruneOlderThanTime) {
                keep.add(normalizeGuid(getField(summary, 'publicationId', 'PublicationId')));
            }
        }
    }

    if (keepRecentPublicationCount && keepRecentPublicationCount > 0) {
        const recent = [...summaries]
            .sort((a, b) => getMarketPublicationPublishedTime(b) - getMarketPublicationPublishedTime(a))
            .slice(0, keepRecentPublicationCount);
        for (const summary of recent) {
            keep.add(normalizeGuid(getField(summary, 'publicationId', 'PublicationId')));
        }
    }

    return keep;
}

function getMarketPublicationPublishedTime(summary) {
    const context = getField(summary, 'publicationContext', 'PublicationContext');
    const publishedAt = getField(context, 'publishedAtUtc', 'PublishedAtUtc');
    const parsed = publishedAt ? new Date(publishedAt).getTime() : 0;
    return Number.isFinite(parsed) ? parsed : 0;
}

function markMarketPublicationDetailsPruned(summary) {
    const manifest = getField(summary, 'detailManifest', 'DetailManifest');
    const entries = getField(manifest, 'entries', 'Entries') ?? [];
    for (const entry of entries) {
        const availability = getField(entry, 'availability', 'Availability');
        if (availability === 1) {
            entry.availability = 3;
            entry.unavailableReason = 'Detail was pruned from local cold storage.';
        }
    }
}

async function saveMarketListingFacts(facts, startIndex = 0) {
    const database = await initDB();

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_FETCHES], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_FETCHES);
        let index = startIndex ?? 0;
        for (const fact of facts ?? []) {
            store.put(normalizeFactForStorage(fact, index++));
        }

        transaction.oncomplete = () => resolve(true);
        transaction.onerror = (event) => reject(transaction.error || event.target?.error);
        transaction.onabort = (event) => reject(transaction.error || event.target?.error);
    });
}

function canonicalFactFromMarketDetail(detail, listing, listingIndex) {
    const key = getField(detail, 'key', 'Key') ?? {};
    const world = getField(key, 'world', 'World') ?? {};
    const scope = getField(key, 'scope', 'Scope') ?? 0;
    const dataCenter = getField(world, 'dataCenter', 'DataCenter') ?? '';
    const worldName = getField(world, 'worldName', 'WorldName') ?? '';
    const retrievedAt = getField(detail, 'retrievedAtUtc', 'RetrievedAtUtc') ??
        getField(detail, 'createdAtUtc', 'CreatedAtUtc') ??
        '';
    const sourceScopeKey = `${scope}:${dataCenter}:${worldName}`;

    return {
        publicationId: normalizeGuid(getField(key, 'publicationId', 'PublicationId')),
        runId: normalizeGuid(getField(detail, 'runId', 'RunId')),
        demandFingerprint: getField(key, 'demandFingerprint', 'DemandFingerprint'),
        itemId: getField(key, 'itemId', 'ItemId') ?? 0,
        scope,
        dataCenter,
        worldName,
        retrievedAtUtc: retrievedAt,
        marketUploadedAtUtc: getField(detail, 'marketUploadedAtUtc', 'MarketUploadedAtUtc'),
        lastReviewTimeUtc: getField(listing, 'lastReviewTimeUtc', 'LastReviewTimeUtc'),
        quantity: getField(listing, 'quantity', 'Quantity') ?? 0,
        unitPrice: getField(listing, 'pricePerUnit', 'PricePerUnit') ?? 0,
        isHq: getField(listing, 'isHq', 'IsHq') ?? false,
        retainerName: getField(listing, 'retainerName', 'RetainerName') ?? '',
        listingId: getField(listing, 'listingId', 'ListingId') ?? `${sourceScopeKey}:${listingIndex}`,
        priceSanity: getField(listing, 'priceSanity', 'PriceSanity') ?? 0,
        competitiveness: getField(listing, 'competitiveness', 'Competitiveness') ?? 0,
        classificationReasons: getField(detail, 'classificationReasons', 'ClassificationReasons') ?? [],
        sourceProvider: 'Universalis',
        sourceScopeKey
    };
}

function marketDetailMatchesSourceQuery(detail, query) {
    const key = getField(detail, 'key', 'Key') ?? {};
    const world = getField(key, 'world', 'World');
    if (!world) {
        return false;
    }

    const itemId = getField(query, 'itemId', 'ItemId');
    const scope = getField(query, 'scope', 'Scope');
    const dataCenter = getField(query, 'dataCenter', 'DataCenter');
    const worldName = getField(query, 'worldName', 'WorldName');
    const publicationId = normalizeGuid(getField(query, 'publicationId', 'PublicationId'));
    const runId = normalizeGuid(getField(query, 'runId', 'RunId'));
    const demandFingerprint = getFingerprintValue(getField(query, 'demandFingerprint', 'DemandFingerprint'));
    const keyDemandFingerprint = getFingerprintValue(getField(key, 'demandFingerprint', 'DemandFingerprint'));

    return (itemId == null || getField(key, 'itemId', 'ItemId') === itemId) &&
        (scope == null || getField(key, 'scope', 'Scope') === scope) &&
        (!dataCenter || String(getField(world, 'dataCenter', 'DataCenter') ?? '').toLowerCase() === String(dataCenter).toLowerCase()) &&
        (!worldName || String(getField(world, 'worldName', 'WorldName') ?? '').toLowerCase() === String(worldName).toLowerCase()) &&
        (!publicationId || normalizeGuid(getField(key, 'publicationId', 'PublicationId')) === publicationId) &&
        (!runId || normalizeGuid(getField(detail, 'runId', 'RunId')) === runId) &&
        (!demandFingerprint || keyDemandFingerprint === demandFingerprint);
}

async function loadMarketListingFactsFromDetails(query) {
    const database = await initDB();
    const publicationId = normalizeGuid(getField(query, 'publicationId', 'PublicationId'));

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_LISTING_DETAILS], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_LISTING_DETAILS);
        const request = publicationId
            ? store.index('publicationId').openCursor(publicationId)
            : store.openCursor();
        const results = [];

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor) {
                resolve(results);
                return;
            }

            const detail = cursor.value;
            if (marketDetailMatchesSourceQuery(detail, query)) {
                const listings = getField(detail, 'listings', 'Listings') ?? [];
                for (let i = 0; i < listings.length; i++) {
                    results.push(canonicalFactFromMarketDetail(detail, listings[i], i));
                }
            }

            cursor.continue();
        };

        request.onerror = () => reject(request.error);
    });
}

async function loadMarketListingFacts(query) {
    const database = await initDB();
    const itemId = getField(query, 'itemId', 'ItemId');
    const scope = getField(query, 'scope', 'Scope');
    const dataCenter = getField(query, 'dataCenter', 'DataCenter');
    const worldName = getField(query, 'worldName', 'WorldName');
    const publicationId = normalizeGuid(getField(query, 'publicationId', 'PublicationId'));
    const runId = normalizeGuid(getField(query, 'runId', 'RunId'));
    const demandFingerprint = getFingerprintValue(getField(query, 'demandFingerprint', 'DemandFingerprint'));

    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_FETCHES], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_FETCHES);
        const request = store.openCursor();
        const results = [];

        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (!cursor) {
                resolve(results);
                return;
            }

            const fact = cursor.value;
            if ((itemId == null || getField(fact, 'itemId', 'ItemId') === itemId) &&
                (scope == null || getField(fact, 'scope', 'Scope') === scope) &&
                (!dataCenter || String(getField(fact, 'dataCenter', 'DataCenter') ?? '').toLowerCase() === String(dataCenter).toLowerCase()) &&
                (!worldName || String(getField(fact, 'worldName', 'WorldName') ?? '').toLowerCase() === String(worldName).toLowerCase()) &&
                (!publicationId || normalizeGuid(getField(fact, 'publicationId', 'PublicationId')) === publicationId) &&
                (!runId || normalizeGuid(getField(fact, 'runId', 'RunId')) === runId) &&
                (!demandFingerprint ||
                    getFingerprintValue(getField(fact, 'demandFingerprint', 'DemandFingerprint')) === demandFingerprint)) {
                results.push(fact);
            }

            cursor.continue();
        };

        request.onerror = () => reject(request.error);
    });
}

// Export functions for Blazor interop
window.IndexedDB = {
    savePlan,
    loadPlan,
    loadAllPlans,
    loadPlanSummaries,
    patchMarketAnalysis,
    deletePlan,
    saveSetting,
    loadSetting,
    clearAllPlans,
    clearMarketCache,
    saveMarketData,
    loadMarketData,
    loadMarketDataBulk,
    deleteStaleMarketData,
    deleteOldestEntries,
    getMarketCacheStats,
    saveMarketPublication,
    saveMarketPublicationDetails,
    saveMarketPublicationRunRecords,
    saveMarketPublicationSummary,
    loadMarketPublicationSummary,
    loadMarketDetailManifest,
    loadMarketDetails,
    loadMarketRunRecord,
    pruneMarketDetails,
    saveMarketListingFacts,
    loadMarketListingFactsFromDetails,
    loadMarketListingFacts
};

console.log('[IndexedDB] Module loaded (v5 with market intelligence)');
