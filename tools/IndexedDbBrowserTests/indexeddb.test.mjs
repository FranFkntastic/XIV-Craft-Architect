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
    if (request.url === '/indexedDB.js?v=30') {
      response.writeHead(200, { 'content-type': 'text/javascript', 'cache-control': 'no-store' });
      response.end(script);
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
    response.end('<!doctype html><script src="/indexedDB.js?v=30"></script>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  await new Promise(resolve => server.close(resolve));
});

for (const [name, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${name}: conditional setting save respects the active account scope`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() =>
        typeof window.IndexedDB?.saveSettingsWhenSettingsMatchAndRevisionNotNewer === 'function');

      const result = await page.evaluate(async () => {
        await IndexedDB.saveSetting('profileHost.hostUrl', '"https://host-a.example/api"');
        await IndexedDB.saveSetting('profileHost.connectedProfileId', '"profile-a"');
        await IndexedDB.saveSetting('profileHost.connectedProfileName', '"Old name"');
        await IndexedDB.saveSetting('profileHost.connectedProfileMetadataRevision', '0');

        const matched = await IndexedDB.saveSettingsWhenSettingsMatchAndRevisionNotNewer(
          {
            'profileHost.connectedProfileName': '"Canonical name"',
            'profileHost.connectedProfileMetadataRevision': '1'
          },
          {
            'profileHost.hostUrl': '"https://host-a.example/api"',
            'profileHost.connectedProfileId': '"profile-a"'
          },
          'profileHost.connectedProfileMetadataRevision',
          '1');
        const afterMatch = await IndexedDB.loadSetting('profileHost.connectedProfileName');

        const staleRevision = await IndexedDB.saveSettingsWhenSettingsMatchAndRevisionNotNewer(
          {
            'profileHost.connectedProfileName': '"Stale name"',
            'profileHost.connectedProfileMetadataRevision': '0'
          },
          {
            'profileHost.hostUrl': '"https://host-a.example/api"',
            'profileHost.connectedProfileId': '"profile-a"'
          },
          'profileHost.connectedProfileMetadataRevision',
          '0');
        const afterStaleRevision = await IndexedDB.loadSetting('profileHost.connectedProfileName');

        const staleScope = await IndexedDB.saveSettingsWhenSettingsMatchAndRevisionNotNewer(
          {
            'profileHost.connectedProfileName': '"Wrong account"',
            'profileHost.connectedProfileMetadataRevision': '2'
          },
          {
            'profileHost.hostUrl': '"https://host-a.example/api"',
            'profileHost.connectedProfileId': '"profile-b"'
          },
          'profileHost.connectedProfileMetadataRevision',
          '2');
        const afterStaleScope = await IndexedDB.loadSetting('profileHost.connectedProfileName');
        return { matched, afterMatch, staleRevision, afterStaleRevision, staleScope, afterStaleScope };
      });

      assert.deepEqual(result, {
        matched: 1,
        afterMatch: '"Canonical name"',
        staleRevision: 2,
        afterStaleRevision: '"Canonical name"',
        staleScope: 0,
        afterStaleScope: '"Canonical name"'
      });
    } finally {
      await browser.close();
    }
  });

  test(`${name}: hosted owner proof commits atomically beside its order`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);

      const result = await page.evaluate(async () => {
        const hostKey = 'profileHost.hostUrl';
        const profileKey = 'profileHost.connectedProfileId';
        const revisionKey = 'profileHost.authority.test.profile.profile-a.objectRevision.tradeOrders.order-a';
        const receiptKey = 'profileHost.authority.test.profile.profile-a.ownerReceipt.order-a';
        const oldOrder = { id: 'order-a', companyProfileId: 'company-a', title: 'Last known' };
        await IndexedDB.saveTradeOrder(oldOrder);
        await IndexedDB.saveSetting(hostKey, '"https://host.example/api/"');
        await IndexedDB.saveSetting(profileKey, '"profile-a"');
        await IndexedDB.saveSetting(revisionKey, '100');

        const bootstrap = await IndexedDB.applyHostedOwnerVerificationBatch(
          [],
          { [receiptKey]: '"receipt-100"', [revisionKey]: '100' },
          [],
          {
            [hostKey]: '"https://host.example/api/"',
            [profileKey]: '"profile-a"',
            [revisionKey]: '100'
          });
        const changed = await IndexedDB.applyHostedOwnerVerificationBatch(
          [{ ...oldOrder, title: 'Canonical change' }],
          { [receiptKey]: '"receipt-101"', [revisionKey]: '101' },
          [],
          { [revisionKey]: '100' });
        const lateOwner = IndexedDB.applyHostedOwnerVerificationBatch(
          [{ ...oldOrder, title: 'Late owner revision 102' }],
          { [receiptKey]: '"receipt-102"', [revisionKey]: '102' },
          [],
          { [revisionKey]: '101' });
        const replay = IndexedDB.applyHostedTradeOrderState(
          { ...oldOrder, title: 'Replay revision 103' },
          'order-a',
          revisionKey,
          '103',
          false);
        const [lateOwnerAccepted, replayReconciled] = await Promise.all([lateOwner, replay]);
        const stale = await IndexedDB.applyHostedOwnerVerificationBatch(
          [{ ...oldOrder, title: 'Stale response' }],
          { [receiptKey]: '"stale"', [revisionKey]: '100' },
          [],
          { [revisionKey]: '102' });
        const state = await IndexedDB.loadHostedOwnerSettings([receiptKey, revisionKey]);
        const durableOrder = await IndexedDB.loadTradeOrder('order-a');
        return {
          bootstrap,
          changed,
          lateOwnerAccepted,
          replayReconciled,
          stale,
          state,
          durableOrder
        };
      });

      assert.equal(result.bootstrap, true);
      assert.equal(result.changed, true);
      assert.equal(result.lateOwnerAccepted, false);
      assert.equal(result.replayReconciled, true);
      assert.equal(result.stale, false);
      assert.notEqual(result.state['profileHost.authority.test.profile.profile-a.ownerReceipt.order-a'], '"stale"');
      assert.equal(result.state['profileHost.authority.test.profile.profile-a.objectRevision.tradeOrders.order-a'], '103');
      assert.equal(result.durableOrder.title, 'Replay revision 103');
    } finally {
      await browser.close();
    }
  });

  test(`${name}: company schema v3 upgrades owner state without losing orders`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const context = await browser.newContext();
      const setup = await context.newPage();
      await setup.goto(`${origin}/empty`, { waitUntil: 'load' });
      await setup.evaluate(async () => {
        await new Promise((resolve, reject) => {
          const deletion = indexedDB.deleteDatabase('FFXIVCraftArchitect.Company');
          deletion.onsuccess = resolve;
          deletion.onerror = () => reject(deletion.error);
        });
        await new Promise((resolve, reject) => {
          const request = indexedDB.open('FFXIVCraftArchitect.Company', 3);
          request.onupgradeneeded = () => {
            const database = request.result;
            database.createObjectStore('storageMetadata', { keyPath: 'id' });
            const companies = database.createObjectStore('tradeCompanyProfiles', { keyPath: 'id' });
            companies.createIndex('updatedAtUtc', 'updatedAtUtc');
            const crafters = database.createObjectStore('tradeCrafters', { keyPath: 'id' });
            crafters.createIndex('companyProfileId', 'companyProfileId');
            crafters.createIndex('displayName', 'displayName');
            const orders = database.createObjectStore('tradeOrders', { keyPath: 'id' });
            orders.createIndex('companyProfileId', 'companyProfileId');
            orders.createIndex('status', 'status');
            orders.createIndex('commissionedAtUtc', 'commissionedAtUtc');
            orders.put({ id: 'retained-order', companyProfileId: 'company-a', title: 'Retained' });
            database.createObjectStore('tradeOrderArchiveSummaries', { keyPath: 'id' });
            const snapshots = database.createObjectStore('tradeOrderCraftSnapshots', { keyPath: 'id' });
            snapshots.createIndex('companyProfileId', 'companyProfileId');
            snapshots.createIndex('orderId', 'orderId');
            snapshots.createIndex('updatedAtUtc', 'updatedAtUtc');
            const payroll = database.createObjectStore('tradePayrollDrafts', { keyPath: 'id' });
            payroll.createIndex('companyProfileId', 'companyProfileId');
            payroll.createIndex('orderId', 'orderId');
            payroll.createIndex('planSessionVersion', 'planSessionVersion');
            payroll.createIndex('updatedAtUtc', 'updatedAtUtc');
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
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);
      const result = await page.evaluate(async () => {
        const diagnostics = await IndexedDB.getSpecializedStorageDiagnostics();
        const retained = await IndexedDB.loadTradeOrder('retained-order');
        const request = indexedDB.open('FFXIVCraftArchitect.Company');
        const database = await new Promise((resolve, reject) => {
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        const hasOwnerState = database.objectStoreNames.contains('hostedOwnerState');
        database.close();
        return { diagnostics, retained, hasOwnerState };
      });

      assert.equal(result.diagnostics.versions.company, 4);
      assert.equal(result.hasOwnerState, true);
      assert.equal(result.retained.title, 'Retained');
      await context.close();
    } finally {
      await browser.close();
    }
  });

  test(`${name}: repair creates complete Worker session schema`, { timeout: 30_000 }, async () => {
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
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);
      const repaired = await page.evaluate(async () => {
        await IndexedDB.getTradeStoreDiagnostics();
        const request = indexedDB.open('FFXIVCraftArchitect');
        const database = await new Promise((resolve, reject) => {
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        const result = {
          hasSessionManifestStore: database.objectStoreNames.contains('engineSessionManifests'),
          hasSessionRevisionStore: database.objectStoreNames.contains('engineSessionRevisions'),
          hasSessionComponentStore: database.objectStoreNames.contains('engineSessionComponents'),
          hasPlanComponentStore: database.objectStoreNames.contains('planComponents')
        };
        database.close();
        return result;
      });

      assert.deepEqual(repaired, {
        hasSessionManifestStore: true,
        hasSessionRevisionStore: true,
        hasSessionComponentStore: true,
        hasPlanComponentStore: true
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
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);

      const result = await page.evaluate(async () => {
        await window.IndexedDB.clearMarketCache();
        const open = indexedDB.open('FFXIVCraftArchitect.Market');
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
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);

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

  test(`${name}: legacy saved plan migrates copy-on-write on first patch`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);

      const migrated = await page.evaluate(async () => {
        await IndexedDB.loadPlan('initialize-schema');
        const open = indexedDB.open('FFXIVCraftArchitect.Personal');
        const database = await new Promise((resolve, reject) => {
          open.onsuccess = () => resolve(open.result);
          open.onerror = () => reject(open.error);
        });
        await new Promise((resolve, reject) => {
          const transaction = database.transaction(['plans', 'planSummaries'], 'readwrite');
          transaction.objectStore('plans').put({
            id: 'legacy-plan',
            name: 'Legacy Plan',
            projectItems: [{ id: 1, name: 'Legacy' }],
            planJson: '{"plan":"legacy"}',
            marketIntelligenceJson: '{"evidence":"legacy"}',
            procurementRouteJson: null,
            savedAt: '2026-01-01T00:00:00Z',
            modifiedAt: '2026-01-01T00:00:00Z'
          });
          transaction.oncomplete = resolve;
          transaction.onerror = () => reject(transaction.error);
          transaction.onabort = () => reject(transaction.error);
        });
        database.close();

        const before = await IndexedDB.loadPlan('legacy-plan');
        await IndexedDB.patchPlanAndProcurementRoute('legacy-plan', {
          procurementRouteJson: '{"route":"migrated"}'
        });
        const after = await IndexedDB.loadPlan('legacy-plan');
        const reopened = indexedDB.open('FFXIVCraftArchitect.Personal');
        const migratedDatabase = await new Promise((resolve, reject) => {
          reopened.onsuccess = () => resolve(reopened.result);
          reopened.onerror = () => reject(reopened.error);
        });
        const raw = await new Promise((resolve, reject) => {
          const request = migratedDatabase.transaction('plans').objectStore('plans').get('legacy-plan');
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        const componentCount = await new Promise((resolve, reject) => {
          const request = migratedDatabase.transaction('planComponents').objectStore('planComponents').count();
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        migratedDatabase.close();
        return {
          before,
          after,
          rawSchemaVersion: raw.schemaVersion,
          rawEmbedsPlanJson: Object.prototype.hasOwnProperty.call(raw, 'planJson'),
          componentCount
        };
      });

      assert.equal(migrated.before.planJson, '{"plan":"legacy"}');
      assert.equal(migrated.after.planJson, '{"plan":"legacy"}');
      assert.equal(migrated.after.marketIntelligenceJson, '{"evidence":"legacy"}');
      assert.equal(migrated.after.procurementRouteJson, '{"route":"migrated"}');
      assert.equal(migrated.rawSchemaVersion, 2);
      assert.equal(migrated.rawEmbedsPlanJson, false);
      assert.equal(migrated.componentCount, 3);
    } finally {
      await browser.close();
    }
  });

  test(`${name}: procurement route patch preserves the large stored evidence payload`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 30);

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
        const openBefore = indexedDB.open('FFXIVCraftArchitect.Personal');
        const databaseBefore = await new Promise((resolve, reject) => {
          openBefore.onsuccess = () => resolve(openBefore.result);
          openBefore.onerror = () => reject(openBefore.error);
        });
        const beforeRecord = await new Promise((resolve, reject) => {
          const request = databaseBefore.transaction('plans').objectStore('plans').get('autosave');
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        databaseBefore.close();
        await IndexedDB.patchPlanAndProcurementRoute('autosave', {
          planJson: '{"plan":"current"}',
          procurementRouteJson: '{"route":"current"}'
        });
        const plan = await IndexedDB.loadPlan('autosave');
        const openAfter = indexedDB.open('FFXIVCraftArchitect.Personal');
        const databaseAfter = await new Promise((resolve, reject) => {
          openAfter.onsuccess = () => resolve(openAfter.result);
          openAfter.onerror = () => reject(openAfter.error);
        });
        const afterRecord = await new Promise((resolve, reject) => {
          const request = databaseAfter.transaction('plans').objectStore('plans').get('autosave');
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        const componentCount = await new Promise((resolve, reject) => {
          const request = databaseAfter.transaction('planComponents').objectStore('planComponents').count();
          request.onsuccess = () => resolve(request.result);
          request.onerror = () => reject(request.error);
        });
        databaseAfter.close();
        return {
          planJson: plan.planJson,
          marketIntelligenceJsonMatches: plan.marketIntelligenceJson === marketIntelligenceJson,
          procurementRouteJson: plan.procurementRouteJson,
          schemaVersion: afterRecord.schemaVersion,
          marketEvidenceRefReused:
            beforeRecord.componentRefs.marketIntelligenceJson ===
            afterRecord.componentRefs.marketIntelligenceJson,
          planRefReplaced:
            beforeRecord.componentRefs.planJson !== afterRecord.componentRefs.planJson,
          componentCount
        };
      });

      assert.equal(patched.planJson, '{"plan":"current"}');
      assert.equal(patched.marketIntelligenceJsonMatches, true);
      assert.equal(patched.procurementRouteJson, '{"route":"current"}');
      assert.equal(patched.schemaVersion, 2);
      assert.equal(patched.marketEvidenceRefReused, true);
      assert.equal(patched.planRefReplaced, true);
      assert.equal(patched.componentCount, 3);
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
