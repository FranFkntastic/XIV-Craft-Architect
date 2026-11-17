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

}
