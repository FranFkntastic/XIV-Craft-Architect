// IndexedDB module for FFXIV Craft Architect Web
// Provides persistent storage for crafting plans in the browser

const DB_NAME = 'FFXIVCraftArchitect';
const DB_VERSION = 2;  // Bumped for market cache store
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
            
            // Market cache store (v2)
            if (!database.objectStoreNames.contains(STORE_MARKET_CACHE)) {
                const cacheStore = database.createObjectStore(STORE_MARKET_CACHE, { keyPath: 'key' });
                cacheStore.createIndex('fetchedAt', 'fetchedAt', { unique: false });
            }
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
        
        // Add timestamps
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
        const request = index.openCursor(null, 'prev'); // Descending order
        
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
 * Clear all plans (nuclear option)
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
 * Save market data to cache
 */
async function saveMarketData(key, data) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        
        const cacheEntry = {
            key: key,
            itemId: data.itemId,
            dataCenter: data.dataCenter,
            fetchedAt: data.fetchedAt,
            dcAvgPrice: data.dcAvgPrice,
            hqAvgPrice: data.hqAvgPrice,
            worlds: data.worlds
        };
        
        const request = store.put(cacheEntry);
        
        request.onsuccess = () => resolve(true);
        request.onerror = () => reject(request.error);
    });
}

/**
 * Load market data from cache
 */
async function loadMarketData(key) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.get(key);
        
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error);
    });
}

/**
 * Delete stale market data
 */
async function deleteStaleMarketData(cutoffDate) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readwrite');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const index = store.index('fetchedAt');
        const range = IDBKeyRange.upperBound(cutoffDate);
        const request = index.openCursor(range);
        
        let deletedCount = 0;
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                store.delete(cursor.primaryKey);
                deletedCount++;
                cursor.continue();
            } else {
                resolve(deletedCount);
            }
        };
        
        request.onerror = () => reject(request.error);
    });
}

/**
 * Get market cache statistics
 */
async function getMarketCacheStats(cutoffDate) {
    const database = await initDB();
    
    return new Promise((resolve, reject) => {
        const transaction = database.transaction([STORE_MARKET_CACHE], 'readonly');
        const store = transaction.objectStore(STORE_MARKET_CACHE);
        const request = store.openCursor();
        
        let total = 0;
        let valid = 0;
        let stale = 0;
        let oldest = null;
        let newest = null;
        let totalSize = 0;
        
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                const entry = cursor.value;
                total++;
                totalSize += JSON.stringify(entry).length;
                
                const fetchedAt = new Date(entry.fetchedAt);
                if (fetchedAt > new Date(cutoffDate)) {
                    valid++;
                } else {
                    stale++;
                }
                
                if (!oldest || fetchedAt < oldest) oldest = fetchedAt;
                if (!newest || fetchedAt > newest) newest = fetchedAt;
                
                cursor.continue();
            } else {
                resolve({
                    total,
                    valid,
                    stale,
                    oldest: oldest ? oldest.toISOString() : null,
                    newest: newest ? newest.toISOString() : null,
                    sizeBytes: totalSize
                });
            }
        };
        
        request.onerror = () => reject(request.error);
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
    saveMarketData,
    loadMarketData,
    deleteStaleMarketData,
    getMarketCacheStats
};
