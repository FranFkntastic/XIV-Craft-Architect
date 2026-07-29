import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(
  here,
  '../../src/FFXIV Craft Architect.Web/wwwroot/indexedDB.js');
const companyA = '018fdc85-9b7a-7c31-87ed-6f9bdb4a1111';
const companyB = '018fdc85-9b7a-7c31-87ed-6f9bdb4a2222';
const grantId = '018fdc85-9b7a-7c31-87ed-6f9bdb4a3333';
let server;
let origin;

before(async () => {
  const script = await readFile(scriptPath);
  server = http.createServer((request, response) => {
    if (request.url === '/indexedDB.js') {
      response.writeHead(200, { 'content-type': 'text/javascript', 'cache-control': 'no-store' });
      response.end(script);
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
    response.end('<!doctype html><script src="/indexedDB.js"></script>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  await new Promise(resolve => server.close(resolve));
});

async function seedLegacy(page) {
  await page.evaluate(async ({ companyA }) => {
    const names = [
      'FFXIVCraftArchitect',
      'FFXIVCraftArchitect.Personal',
      'FFXIVCraftArchitect.Market',
      'FFXIVCraftArchitect.Engine',
      'FFXIVCraftArchitect.Company'
    ];
    for (const name of names) {
      await new Promise((resolve, reject) => {
        const request = indexedDB.deleteDatabase(name);
        request.onsuccess = resolve;
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error(`Deletion blocked for ${name}`));
      });
    }

    await new Promise((resolve, reject) => {
      const request = indexedDB.open('FFXIVCraftArchitect', 3);
      request.onupgradeneeded = () => {
        const database = request.result;
        const plans = database.createObjectStore('plans', { keyPath: 'id' });
        plans.createIndex('name', 'name');
        plans.createIndex('modifiedAt', 'modifiedAt');
        database.createObjectStore('settings', { keyPath: 'key' });
        const market = database.createObjectStore('marketCache', { keyPath: 'key' });
        market.createIndex('fetchedAtUnix', 'fetchedAtUnix');
      };
      request.onsuccess = () => {
        const database = request.result;
        const transaction = database.transaction(
          ['plans', 'settings', 'marketCache'],
          'readwrite');
        transaction.objectStore('plans').put({
          id: 'legacy-plan',
          name: 'Legacy Plan',
          modifiedAt: '2026-07-29T00:00:00Z',
          savedAt: '2026-07-29T00:00:00Z',
          projectItems: [{ id: 2, name: 'Fire Shard', quantity: 7 }]
        });
        transaction.objectStore('settings').put({
          key: 'market.default_datacenter',
          value: JSON.stringify('Primal')
        });
        transaction.objectStore('settings').put({
          key: 'profileHost.accessKey',
          value: JSON.stringify('must-remain-browser-local')
        });
        transaction.objectStore('marketCache').put({
          key: '2@Primal',
          itemId: 2,
          dataCenter: 'Primal',
          fetchedAtUnix: 1_784_635_200,
          worlds: []
        });
        transaction.oncomplete = () => {
          database.close();
          resolve();
        };
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
      };
      request.onerror = () => reject(request.error);
    });

    // Let the production migration reader upgrade the legacy schema, then seed
    // Trade records in the complete v15 store set.
    await new Promise((resolve, reject) => {
      const request = indexedDB.open('FFXIVCraftArchitect', 15);
      request.onupgradeneeded = () => {
        const database = request.result;
        const create = (name, keyPath = 'id') => {
          if (!database.objectStoreNames.contains(name)) {
            database.createObjectStore(name, { keyPath });
          }
        };
        create('planComponents');
        create('planSummaries');
        create('tradeCompanyProfiles');
        create('tradeCrafters');
        create('tradeOrders');
        create('tradeOrderCraftSnapshots');
        create('tradePayrollDrafts');
        create('engineSessionManifests');
        create('engineSessionRevisions');
        create('engineSessionComponents');
      };
      request.onsuccess = () => {
        const database = request.result;
        const transaction = database.transaction(
          ['tradeCompanyProfiles', 'tradeOrders'],
          'readwrite');
        transaction.objectStore('tradeCompanyProfiles').put({
          id: companyA,
          name: 'The Studium',
          createdAtUtc: '2026-07-29T00:00:00Z',
          updatedAtUtc: '2026-07-29T00:00:00Z'
        });
        transaction.objectStore('tradeOrders').put({
          id: '018fdc85-9b7a-7c31-87ed-6f9bdb4a4444',
          companyProfileId: companyA,
          title: 'Raid gear',
          updatedAtUtc: '2026-07-29T00:00:00Z'
        });
        transaction.oncomplete = () => {
          database.close();
          resolve();
        };
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
      };
      request.onerror = () => reject(request.error);
    });
  }, { companyA });
}

for (const [browserName, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${browserName}: specialized migration, company outbox, and portable settings are durable`, {
    timeout: 60_000
  }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const context = await browser.newContext();
      const seedPage = await context.newPage();
      await seedPage.goto(`${origin}/blank`, { waitUntil: 'load' });
      await seedLegacy(seedPage);
      await seedPage.close();

      const page = await context.newPage();
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 21);
      const result = await page.evaluate(async ({ companyA, companyB, grantId }) => {
        const diagnostics = await IndexedDB.getSpecializedStorageDiagnostics();
        const plan = await IndexedDB.loadPlan('legacy-plan');
        const market = await IndexedDB.loadMarketData('2@Primal');
        const profiles = await IndexedDB.loadTradeCompanyProfiles();
        const orders = await IndexedDB.loadTradeOrders(companyA);

        await IndexedDB.saveCachedTradeCompanyIdentity({
          companyId: companyA,
          displayName: 'The Studium',
          revision: 4,
          createdAtUtc: '2026-07-29T00:00:00Z',
          updatedAtUtc: '2026-07-29T01:00:00Z'
        });
        await IndexedDB.saveCachedTradeCompanyIdentity({
          companyId: companyB,
          displayName: 'Other Company',
          revision: 1,
          createdAtUtc: '2026-07-29T00:00:00Z',
          updatedAtUtc: '2026-07-29T01:00:00Z'
        });
        const mutation = {
          companyId: companyA,
          recordKind: 'order',
          recordId: 'order-1',
          payloadJson: '{"title":"Queued"}',
          expectedRecordRevision: 0,
          expectedCompanyRevision: 4,
          idempotencyKey: 'same-key',
          protocolVersion: 1
        };
        await IndexedDB.enqueueTradeCompanyMutation(mutation);
        await IndexedDB.enqueueTradeCompanyMutation({
          ...mutation,
          companyId: companyB,
          expectedCompanyRevision: 1
        });
        await IndexedDB.markTradeCompanyMutationAttempt(companyA, 'same-key');
        const pendingBefore = await IndexedDB.loadTradeCompanyMutationOutbox(companyA);
        await IndexedDB.completeTradeCompanyMutation(companyA, 'same-key', {
          status: 'Applied',
          record: {
            companyId: companyA,
            recordKind: 'order',
            recordId: 'order-1',
            payloadJson: '{"title":"Applied"}',
            recordRevision: 1,
            companyRevision: 5,
            updatedAtUtc: '2026-07-29T02:00:00Z',
            deleted: false
          }
        });
        const pendingAfter = await IndexedDB.loadTradeCompanyMutationOutbox(companyA);
        const otherCompanyPending = await IndexedDB.loadTradeCompanyMutationOutbox(companyB);
        const cachedRecord = await IndexedDB.loadCachedTradeCompanyRecord(
          companyA,
          'order',
          'order-1');

        const portable = await IndexedDB.migratePortableOperatorSettings(
          companyA,
          grantId,
          ['market.default_datacenter']);
        const portableMutation = {
          companyId: companyA,
          recordKind: 'operatorSettings',
          recordId: `operator:${grantId}`,
          payloadJson: JSON.stringify(portable),
          expectedRecordRevision: 0,
          expectedCompanyRevision: 5,
          idempotencyKey: 'portable-migration',
          protocolVersion: 1
        };
        await IndexedDB.savePortableOperatorSettings(portable, portableMutation);

        let incompatibleProtocol = null;
        try {
          await IndexedDB.enqueueTradeCompanyMutation({
            ...mutation,
            idempotencyKey: 'wrong-protocol',
            protocolVersion: 99
          });
        } catch (error) {
          incompatibleProtocol = String(error);
        }
        return {
          diagnostics,
          plan,
          market,
          profiles,
          orders,
          pendingBefore,
          pendingAfter,
          otherCompanyPending,
          cachedRecord,
          portable,
          incompatibleProtocol
        };
      }, { companyA, companyB, grantId });

      assert.equal(result.diagnostics.migrations.personal.state, 'complete');
      assert.equal(result.diagnostics.migrations.market.state, 'complete');
      assert.equal(result.diagnostics.migrations.company.state, 'complete');
      assert.equal(result.plan.name, 'Legacy Plan');
      assert.equal(result.market.dataCenter, 'Primal');
      assert.equal(result.profiles[0].id, companyA);
      assert.equal(result.orders[0].companyProfileId, companyA);
      assert.equal(result.pendingBefore.length, 1);
      assert.equal(result.pendingBefore[0].attemptCount, 1);
      assert.equal(result.pendingAfter.length, 0);
      assert.equal(result.otherCompanyPending.length, 1);
      assert.equal(result.cachedRecord.payloadJson, '{"title":"Applied"}');
      assert.equal(result.portable.settings['market.default_datacenter'], JSON.stringify('Primal'));
      assert.equal(
        Object.prototype.hasOwnProperty.call(result.portable.settings, 'profileHost.accessKey'),
        false);
      assert.match(result.incompatibleProtocol, /incompatible/i);

      await page.reload({ waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 21);
      const afterReload = await page.evaluate(async ({ companyA, companyB, grantId }) => ({
        companyA: await IndexedDB.loadTradeCompanyMutationOutbox(companyA),
        companyB: await IndexedDB.loadTradeCompanyMutationOutbox(companyB),
        portable: await IndexedDB.loadPortableOperatorSettings(companyA, grantId),
        names: (await indexedDB.databases()).map(database => database.name).sort()
      }), { companyA, companyB, grantId });
      assert.equal(afterReload.companyA.length, 1, 'portable settings mutation remains durable');
      assert.equal(afterReload.companyB.length, 1, 'other company outbox remains isolated');
      assert.equal(
        afterReload.portable.settings['market.default_datacenter'],
        JSON.stringify('Primal'));
      assert.ok(afterReload.names.includes('FFXIVCraftArchitect.Personal'));
      assert.ok(afterReload.names.includes('FFXIVCraftArchitect.Market'));
      assert.ok(afterReload.names.includes('FFXIVCraftArchitect.Company'));
      assert.equal(afterReload.names.some(name => /branch|build|localhost/i.test(name)), false);
    } finally {
      await browser.close();
    }
  });

  test(`${browserName}: incompatible specialized schema fails closed`, { timeout: 30_000 }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const context = await browser.newContext();
      const page = await context.newPage();
      await page.goto(`${origin}/blank`, { waitUntil: 'load' });
      await page.evaluate(async () => {
        await new Promise((resolve, reject) => {
          const deletion = indexedDB.deleteDatabase('FFXIVCraftArchitect.Company');
          deletion.onsuccess = resolve;
          deletion.onerror = () => reject(deletion.error);
        });
        await new Promise((resolve, reject) => {
          const request = indexedDB.open('FFXIVCraftArchitect.Company', 2);
          request.onsuccess = () => {
            request.result.close();
            resolve();
          };
          request.onerror = () => reject(request.error);
        });
      });
      await page.goto(origin, { waitUntil: 'load' });
      await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 21);
      const message = await page.evaluate(async () => {
        try {
          await IndexedDB.getSpecializedStorageDiagnostics();
          return null;
        } catch (error) {
          return String(error);
        }
      });
      assert.match(message, /newer incompatible schema|incompatible schema/i);
    } finally {
      await browser.close();
    }
  });
}

test('chromium: incomplete current schema fails closed', { timeout: 30_000 }, async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto(`${origin}/blank`, { waitUntil: 'load' });
    await page.evaluate(async () => {
      await new Promise((resolve, reject) => {
        const deletion = indexedDB.deleteDatabase('FFXIVCraftArchitect.Market');
        deletion.onsuccess = resolve;
        deletion.onerror = () => reject(deletion.error);
      });
      await new Promise((resolve, reject) => {
        const request = indexedDB.open('FFXIVCraftArchitect.Market', 1);
        request.onupgradeneeded = () => {
          request.result.createObjectStore('storageMetadata', { keyPath: 'id' });
        };
        request.onsuccess = () => {
          request.result.close();
          resolve();
        };
        request.onerror = () => reject(request.error);
      });
    });
    await page.goto(origin, { waitUntil: 'load' });
    await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 21);
    const message = await page.evaluate(async () => {
      try {
        await IndexedDB.getSpecializedStorageDiagnostics();
        return null;
      } catch (error) {
        return String(error);
      }
    });
    assert.match(message, /incompatible schema.*missing stores.*marketCache/i);
  } finally {
    await browser.close();
  }
});
