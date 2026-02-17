// IndexedDB module for FFXIV Craft Architect Web
// Uses Unix timestamps (seconds since epoch) for serialization safety

const DB_NAME = 'FFXIVCraftArchitect';
const DB_VERSION = 3;  // Bumped for Unix timestamp migration
const STORE_PLANS = 'plans';
const STORE_SETTINGS = 'settings';
const STORE_MARKET_CACHE = 'marketCache';

let db = null;

/**
 * Initialize the IndexedDB database
 */
async function initDB() {
    if (db) return db;
    
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            db = request.result;
            console.log('[IndexedDB] Database opened successfully (v3 - Unix timestamps)');
            resolve(db);
        };
        
        request.onupgradeneeded = (event) => {
            const database = event.target.result;
            
            // Plans store
            if (!database.objectStoreNames.contains(STORE_PLANS)) {
                const planStore = database.createObjectStore(STORE_PLANS, { keyPath: 'id' });
                planStore.createIndex('name', 'name', { unique: false });
                planStore.createIndex('modifiedAt', 'modifiedAt', { unique: false });
            }
            
            // Settings store
            if (!database.objectStoreNames.contains(STORE_SETTINGS)) {
                database.createObjectStore(STORE_SETTINGS, { keyPath: 'key' });
            }
            
            // Market cache store - migrate to Unix timestamps (v3)
            if (database.objectStoreNames.contains(STORE_MARKET_CACHE)) {
                database.deleteObjectStore(STORE_MARKET_CACHE);
                console.log('[IndexedDB] Deleted old market cache store for migration');
            }
            
            const cacheStore = database.createObjectStore(STORE_MARKET_CACHE, { keyPath: 'key' });
            cacheStore.createIndex('fetchedAtUnix', 'fetchedAtUnix', { unique: false });
            console.log('[IndexedDB] Created market cache store with Unix timestamp index');
        };
    });
}

/**
 * Save a plan to IndexedDB
 */
async function savePlan(planData) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        
        const data = {
            ...planData,
            savedAt: new Date().toISOString()
        };
        
        const request = store.put(data);
        
        request.onsuccess = () => resolve(true);
        request.onerror = () => reject(request.error);
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
 * Delete a plan by ID
 */
async function deletePlan(planId) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_PLANS], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.delete(planId);
        
        request.onsuccess = () => resolve(true);
        request.onerror = () => reject(request.error);
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
        const request = store.put({ key, value });
        
        request.onsuccess = () => resolve(true);
        request.onerror = () => reject(request.error);
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
        const transaction = database.transaction([STORE_PLANS], 'readwrite');
        const store = transaction.objectStore(STORE_PLANS);
        const request = store.clear();
        
        request.onsuccess = () => resolve(true);
        request.onerror = () => reject(request.error);
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
    deletePlan,
    saveSetting,
    loadSetting,
    clearAllPlans,
    clearMarketCache,
    saveMarketData,
    loadMarketData,
    deleteStaleMarketData,
    deleteOldestEntries,
    getMarketCacheStats
};

console.log('[IndexedDB] Module loaded (v3 with Unix timestamps)');
