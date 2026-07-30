const DATABASE_NAME = 'FFXIVCraftArchitectMigration';
const DATABASE_VERSION = 1;
const STORE_NAME = 'checkpoints';
const ACTIVE_KEY = 'active';

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
        request.onupgradeneeded = event => {
            const database = event.target.result;
            if (!database.objectStoreNames.contains(STORE_NAME)) {
                database.createObjectStore(STORE_NAME);
            }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(
            request.error || new Error('Could not open the company migration checkpoint database.'));
    });
}

async function runRequest(mode, operation) {
    const database = await openDatabase();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction(STORE_NAME, mode);
            const store = transaction.objectStore(STORE_NAME);
            const request = operation(store);
            let result;
            request.onsuccess = () => {
                result = request.result;
            };
            request.onerror = () => reject(
                request.error || new Error('Company migration checkpoint storage failed.'));
            transaction.oncomplete = () => resolve(result);
            transaction.onabort = () => reject(
                transaction.error || new Error('Company migration checkpoint transaction aborted.'));
            transaction.onerror = () => reject(
                transaction.error || new Error('Company migration checkpoint transaction failed.'));
        });
    } finally {
        database.close();
    }
}

export async function loadActiveCheckpoint() {
    const value = await runRequest('readonly', store => store.get(ACTIVE_KEY));
    return typeof value === 'string' ? value : null;
}

export async function saveActiveCheckpoint(json) {
    if (typeof json !== 'string' || !json) {
        throw new Error('A serialized company migration checkpoint is required.');
    }

    await runRequest('readwrite', store => store.put(json, ACTIVE_KEY));
    return true;
}

export async function clearActiveCheckpoint() {
    await runRequest('readwrite', store => store.delete(ACTIVE_KEY));
    return true;
}
