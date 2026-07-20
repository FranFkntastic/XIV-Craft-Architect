import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(here, '../../src/FFXIV Craft Architect.Web/wwwroot/indexedDB.js');
let server;
let origin;

before(async () => {
  const script = await readFile(scriptPath);
  server = http.createServer((request, response) => {
    if (request.url === '/indexedDB.js?v=10') {
      response.writeHead(200, { 'content-type': 'text/javascript', 'cache-control': 'no-store' });
      response.end(script);
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
    response.end('<!doctype html><script src="/indexedDB.js?v=10"></script>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  await new Promise(resolve => server.close(resolve));
});

for (const [name, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${name}: market maintenance uses bounded indexed operations`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on('console', message => {
        if (message.type() === 'error') errors.push(message.text());
      });
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 10);

      const result = await page.evaluate(async () => {
        await window.IndexedDB.clearMarketCache();
        const open = indexedDB.open('FFXIVCraftArchitect');
        const database = await new Promise((resolve, reject) => {
          open.onsuccess = () => resolve(open.result);
          open.onerror = () => reject(open.error);
        });
        const now = Math.floor(Date.now() / 1000);
        const cutoff = now - 100;
        const payload = { listings: Array.from({ length: 2000 }, (_, i) => ({ price: i, retainer: `r${i}` })) };
        await new Promise((resolve, reject) => {
          const tx = database.transaction('marketCache', 'readwrite');
          const store = tx.objectStore('marketCache');
          store.put({ key: 'inclusive', fetchedAtUnix: cutoff, worlds: payload });
          store.put({ key: 'older', fetchedAtUnix: cutoff - 1, worlds: payload });
          store.put({ key: 'fresh-oldest', fetchedAtUnix: cutoff + 1, worlds: payload });
          store.put({ key: 'fresh-newest', fetchedAtUnix: now, worlds: payload });
          store.put({ key: 'legacy', fetchedAt: new Date((cutoff - 500) * 1000).toISOString(), worlds: payload });
          tx.oncomplete = resolve;
          tx.onerror = () => reject(tx.error);
          tx.onabort = () => reject(tx.error);
        });
        const hasIndex = database.transaction('marketCache').objectStore('marketCache').indexNames.contains('fetchedAtUnix');
        database.close();

        const started = performance.now();
        const initial = await window.IndexedDB.getMarketCacheStats(cutoff);
        const staleDeleted = await window.IndexedDB.deleteStaleMarketData(cutoff);
        const oldestDeleted = await window.IndexedDB.deleteOldestEntries(1);
        const legacyDeleted = await window.IndexedDB.deleteUnindexedMarketData(1);
        const elapsedMs = performance.now() - started;
        const final = await window.IndexedDB.getMarketCacheStats(cutoff);
        return { hasIndex, initial, staleDeleted, oldestDeleted, legacyDeleted, final, elapsedMs };
      });

      assert.equal(result.hasIndex, true);
      assert.deepEqual(result.initial, {
        total: 5, valid: 2, stale: 2, legacyUnindexed: 1,
        oldestUnix: result.initial.oldestUnix,
        newestUnix: result.initial.newestUnix,
        sizeBytes: 5 * 256 * 1024
      });
      assert.equal(result.staleDeleted, 2, 'inclusive cutoff must be deleted');
      assert.equal(result.oldestDeleted, 1, 'oldest indexed fresh entry must be deleted');
      assert.equal(result.legacyDeleted, 1, 'legacy entry must be removed without reading its payload');
      assert.equal(result.final.total, 1, 'only newest indexed entry remains');
      assert.equal(result.final.legacyUnindexed, 0);
      assert.ok(result.elapsedMs < 5000, `maintenance took ${result.elapsedMs}ms`);
      assert.deepEqual(errors, []);
    } finally {
      await browser.close();
    }
  });
}

test('static cache buster matches module revision', async () => {
  const html = await readFile(path.resolve(here, '../../src/FFXIV Craft Architect.Web/wwwroot/index.html'), 'utf8');
  const script = await readFile(scriptPath, 'utf8');
  assert.match(html, /indexedDB\.js\?v=10/);
  assert.match(script, /const MODULE_REVISION = 10;/);
});
