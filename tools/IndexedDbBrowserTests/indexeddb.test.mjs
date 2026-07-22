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
    if (request.url === '/empty') {
      response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
      response.end('<!doctype html>');
      return;
    }
    if (request.url === '/indexedDB.js?v=16') {
      response.writeHead(200, { 'content-type': 'text/javascript', 'cache-control': 'no-store' });
      response.end(script);
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
    response.end('<!doctype html><script src="/indexedDB.js?v=16"></script>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  await new Promise(resolve => server.close(resolve));
});

for (const [name, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${name}: repair creates complete engine ledger schema`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const context = await browser.newContext();
      const setup = await context.newPage();
      await setup.goto(`${origin}/empty`, { waitUntil: 'load' });
      await setup.evaluate(async () => {
        await new Promise((resolve, reject) => {
          const deletion = indexedDB.deleteDatabase('FFXIVCraftArchitect');
          deletion.onsuccess = resolve;
          deletion.onerror = () => reject(deletion.error);
        });
        await new Promise((resolve, reject) => {
          const request = indexedDB.open('FFXIVCraftArchitect', 11);
          request.onupgradeneeded = () => {
            const store = request.result.createObjectStore('engineTransactions', { keyPath: 'transactionId' });
            for (let index = 0; index < 140; index++) {
              store.put({
                transactionId: `legacy-terminal-${index}`,
                canonicalRequestHash: 'a'.repeat(64),
                claimToken: 'b'.repeat(32),
                terminalResultJson: JSON.stringify({ index }),
                updatedAtUnixMilliseconds: index + 1
              });
            }
            store.put({
              transactionId: 'legacy-active',
              canonicalRequestHash: 'a'.repeat(64),
              claimToken: 'c'.repeat(32),
              terminalResultJson: null,
              updatedAtUnixMilliseconds: 141
            });
          };
          request.onsuccess = () => {
            request.result.close();
            resolve();
          };
          request.onerror = () => reject(request.error);
        });
      });
      await setup.close();

      const page = await context.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);
      const repaired = await page.evaluate(async () => {
        await IndexedDB.getTradeStoreDiagnostics();
        const request = indexedDB.open('FFXIVCraftArchitect');
        const database = await new Promise((resolve, reject) => {
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        const store = database.transaction('engineTransactions').objectStore('engineTransactions');
        const terminalIndexCount = await new Promise((resolve, reject) => {
          const count = store.index('terminalUpdatedAtUnixMilliseconds').count();
          count.onsuccess = () => resolve(count.result);
          count.onerror = () => reject(count.error);
        });
        const legacyTerminal = await new Promise((resolve, reject) => {
          const get = store.get('legacy-terminal-0');
          get.onsuccess = () => resolve(get.result);
          get.onerror = () => reject(get.error);
        });
        const counts = await new Promise((resolve, reject) => {
          const cursorRequest = store.openCursor();
          const result = { retained: 0, expired: 0, active: 0 };
          cursorRequest.onsuccess = () => {
            const cursor = cursorRequest.result;
            if (!cursor) return;
            if (cursor.value.terminalResultJson) result.retained++;
            else if (cursor.value.terminalExpired) result.expired++;
            else result.active++;
            cursor.continue();
          };
          cursorRequest.onerror = () => reject(cursorRequest.error);
          cursorRequest.source.transaction.oncomplete = () => resolve(result);
        });
        const result = {
          hasStore: database.objectStoreNames.contains('engineTransactions'),
          hasUpdatedIndex: store.indexNames.contains('updatedAtUnixMilliseconds'),
          hasTerminalIndex: store.indexNames.contains('terminalUpdatedAtUnixMilliseconds'),
          terminalIndexCount,
          legacyTerminalUpdatedAt: legacyTerminal?.terminalUpdatedAtUnixMilliseconds || null,
          legacyTerminalExpired: legacyTerminal?.terminalExpired === true,
          counts
        };
        database.close();
        return result;
      });

      assert.deepEqual(repaired, {
        hasStore: true,
        hasUpdatedIndex: true,
        hasTerminalIndex: true,
          terminalIndexCount: 128,
          legacyTerminalUpdatedAt: null,
        legacyTerminalExpired: true,
        counts: { retained: 128, expired: 12, active: 1 }
      });
    } finally {
      await browser.close();
    }
  });

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
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);

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

  test(`${name}: market analysis patch invalidates persisted procurement route`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);

      const patched = await page.evaluate(async () => {
        await IndexedDB.savePlan({
          id: 'named-plan',
          name: 'Named Plan',
          projectItems: [],
          procurementRouteJson: JSON.stringify({ route: 'stale' })
        });
        await IndexedDB.patchMarketAnalysis(
          'named-plan',
          '[]',
          '[]',
          '{}',
          'MaximizeValue',
          'BulkValue',
          null,
          null);
        return await IndexedDB.loadPlan('named-plan');
      });

      assert.equal(patched.procurementRouteJson, null);
      assert.equal(patched.marketIntelligenceJson, '{}');
    } finally {
      await browser.close();
    }
  });

  test(`${name}: procurement route patch preserves the large stored evidence payload`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);

      const patched = await page.evaluate(async () => {
        const marketIntelligenceJson = JSON.stringify({ evidence: 'x'.repeat(1024 * 1024) });
        await IndexedDB.savePlan({
          id: 'autosave',
          name: 'AutoSave',
          projectItems: [{ id: 1, name: 'Crasher' }],
          planJson: '{"plan":"preserved"}',
          marketIntelligenceJson,
          procurementRouteJson: null
        });
        await IndexedDB.patchPlanAndProcurementRoute('autosave', {
          planJson: '{"plan":"current"}',
          procurementRouteJson: '{"route":"current"}'
        });
        const plan = await IndexedDB.loadPlan('autosave');
        return {
          planJson: plan.planJson,
          marketIntelligenceJsonMatches: plan.marketIntelligenceJson === marketIntelligenceJson,
          procurementRouteJson: plan.procurementRouteJson
        };
      });

      assert.equal(patched.planJson, '{"plan":"current"}');
      assert.equal(patched.marketIntelligenceJsonMatches, true);
      assert.equal(patched.procurementRouteJson, '{"route":"current"}');
    } finally {
      await browser.close();
    }
  });

  test(`${name}: durable engine ledger fences claims and survives reload`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);
      const initial = await page.evaluate(async () => {
        const bounded = (label, operation) => Promise.race([
          operation,
          new Promise((_, reject) => setTimeout(
            () => reject(new Error(`${label} timed out.`)),
            5000))
        ]);
        const transactionId = crypto.randomUUID();
        const abandonedId = crypto.randomUUID();
        const canonicalHash = 'a'.repeat(64);
        const first = await bounded('first claim', IndexedDB.claimEngineTransaction(transactionId, canonicalHash));
        const active = await bounded('active replay', IndexedDB.claimEngineTransaction(transactionId, canonicalHash));
        const conflict = await bounded('conflict', IndexedDB.claimEngineTransaction(transactionId, 'b'.repeat(64)));
        const terminalJson = JSON.stringify({ transactionId, status: 'complete' });
        await bounded('complete', IndexedDB.completeEngineTransaction(
          transactionId, canonicalHash, first.claimToken, terminalJson));
        const terminal = await bounded('terminal replay', IndexedDB.claimEngineTransaction(transactionId, canonicalHash));
        const abandoned = await bounded('abandoned seed', IndexedDB.claimEngineTransaction(abandonedId, canonicalHash));
        return {
          transactionId,
          abandonedId,
          canonicalHash,
          first,
          active,
          conflict,
          terminal,
          terminalJson,
          abandoned
        };
      });

      assert.equal(initial.first.disposition, 'claimed');
      assert.match(initial.first.claimToken, /^[0-9a-f]{32}$/i);
      assert.equal(initial.active.disposition, 'activeReplay');
      assert.equal(initial.conflict.disposition, 'conflict');
      assert.equal(initial.conflict.canonicalRequestHash, 'a'.repeat(64));
      assert.equal(initial.terminal.disposition, 'terminalReplay');
      assert.equal(initial.terminal.terminalResultJson, initial.terminalJson);

      await page.reload({ waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16);
      const recovered = await page.evaluate(async ({ abandonedId, canonicalHash }) => {
        const claim = await IndexedDB.claimEngineTransaction(abandonedId, canonicalHash);
        await IndexedDB.releaseEngineTransaction(
          abandonedId, canonicalHash, claim.claimToken);
        return claim;
      }, initial);

      assert.equal(recovered.disposition, 'abandonedReplay');
      assert.notEqual(recovered.claimToken, initial.abandoned.claimToken);

      const durableReplay = await page.evaluate(async () => {
        const canonicalHash = 'c'.repeat(64);
        let firstTransactionId;
        for (let index = 0; index < 140; index++) {
          const transactionId = crypto.randomUUID();
          firstTransactionId ??= transactionId;
          const claim = await IndexedDB.claimEngineTransaction(transactionId, canonicalHash);
          await IndexedDB.completeEngineTransaction(
            transactionId,
            canonicalHash,
            claim.claimToken,
            JSON.stringify({ transactionId, index }));
        }
        const open = indexedDB.open('FFXIVCraftArchitect');
        const database = await new Promise((resolve, reject) => {
          open.onsuccess = () => resolve(open.result);
          open.onerror = () => reject(open.error);
        });
        const result = await new Promise((resolve, reject) => {
          const transaction = database.transaction('engineTransactions', 'readonly');
           const store = transaction.objectStore('engineTransactions');
          const terminalIndexRequest = store.index('terminalUpdatedAtUnixMilliseconds').count();
          const request = store.openCursor();
          let terminalCount = 0;
          let expiredCount = 0;
          let activeCount = 0;
          request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor) return;
            if (cursor.value.terminalResultJson) terminalCount++;
            else if (cursor.value.terminalExpired) expiredCount++;
            else activeCount++;
            cursor.continue();
          };
          request.onerror = () => reject(request.error);
          transaction.oncomplete = () => resolve({
            terminalCount,
            expiredCount,
            activeCount,
            terminalIndexCount: terminalIndexRequest.result,
            hasRetentionIndex: store.indexNames.contains('terminalUpdatedAtUnixMilliseconds')
          });
          transaction.onerror = () => reject(transaction.error);
        });
        const oldestReplay = await IndexedDB.claimEngineTransaction(firstTransactionId, canonicalHash);
        database.close();
        return { ...result, oldestReplay };
      });
      assert.equal(durableReplay.hasRetentionIndex, true);
      assert.equal(durableReplay.terminalCount, 128);
      assert.equal(durableReplay.expiredCount, 13);
      assert.equal(durableReplay.activeCount, 0);
      assert.equal(durableReplay.terminalIndexCount, 128);
      assert.equal(durableReplay.oldestReplay.disposition, 'expiredTerminalReplay');
    } finally {
      await browser.close();
    }
  });
}

test('static cache buster matches module revision', async () => {
  const html = await readFile(path.resolve(here, '../../src/FFXIV Craft Architect.Web/wwwroot/index.html'), 'utf8');
  const script = await readFile(scriptPath, 'utf8');
  const cacheRevision = html.match(/indexedDB\.js\?v=(\d+)/)?.[1];
  const moduleRevision = script.match(/const MODULE_REVISION = (\d+);/)?.[1];
  assert.ok(cacheRevision, 'index.html must carry an IndexedDB module cache revision');
  assert.ok(moduleRevision, 'indexedDB.js must declare its module revision');
  assert.equal(cacheRevision, moduleRevision);
});
