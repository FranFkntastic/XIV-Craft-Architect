import { createHash } from 'node:crypto';
import { access, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '../..');
const fixtures = {
  historical: path.join(repositoryRoot, 'fixtures/browser/indexeddb-v3.json'),
  network: path.join(repositoryRoot, 'fixtures/browser/truth-network.json'),
  plan: path.join(repositoryRoot, 'fixtures/browser/truth-product.craftplan')
};
const expectedCurrentStores = [
  'marketCache',
  'plans',
  'planSummaries',
  'settings',
  'tradeCompanyProfiles',
  'tradeCrafters',
  'tradeOrderCraftSnapshots',
  'tradeOrders',
  'tradePayrollDrafts'
];
const budgets = {
  operationMs: 15_000,
  startupMs: 45_000,
  productMs: 90_000,
  browserMs: 45_000,
  closeMs: 5_000,
  globalMs: 240_000
};

class TruthFailure extends Error {
  constructor(classification, message, details = {}) {
    super(message);
    this.name = 'TruthFailure';
    this.classification = classification;
    this.details = details;
  }
}

class DeadlineFailure extends TruthFailure {
  constructor(label, timeoutMs) {
    super('timeout', `${label} exceeded ${timeoutMs}ms`, { label, timeoutMs });
  }
}

function parseArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name !== '--web-root' && name !== '--output') {
      throw new TruthFailure('invalid-invocation', `Unknown argument: ${name}`, {
        usage: 'npm test -- --web-root <published-wwwroot> --output <report.json>'
      });
    }
    const value = argv[++index];
    if (!value || value.startsWith('--')) {
      throw new TruthFailure('invalid-invocation', `${name} requires a path`, {
        usage: 'npm test -- --web-root <published-wwwroot> --output <report.json>'
      });
    }
    values[name.slice(2)] = path.resolve(value);
  }
  if (!values['web-root'] || !values.output) {
    throw new TruthFailure('invalid-invocation', '--web-root and --output are required', {
      usage: 'npm test -- --web-root <published-wwwroot> --output <report.json>'
    });
  }
  return { webRoot: values['web-root'], output: values.output };
}

function acceptanceIdentity() {
  const identity = {
    runId: process.env.TRUTHFUL_RUN_ID,
    runAttempt: process.env.TRUTHFUL_RUN_ATTEMPT,
    sourceCommitSha: process.env.TRUTHFUL_SOURCE_SHA,
    archiveSha256: process.env.TRUTHFUL_ARTIFACT_SHA,
    harnessTreeSha256: process.env.TRUTHFUL_HARNESS_SHA,
    fixtureTreeSha256: process.env.TRUTHFUL_FIXTURE_SHA
  };
  if (!/^\d+$/.test(identity.runId || '') || !/^\d+$/.test(identity.runAttempt || '') ||
      !/^[0-9a-f]{40}$/.test(identity.sourceCommitSha || '') ||
      [identity.archiveSha256, identity.harnessTreeSha256, identity.fixtureTreeSha256]
        .some(value => !/^[0-9a-f]{64}$/.test(value || ''))) {
    throw new TruthFailure('missing-acceptance-identity', 'Browser acceptance identity is missing or malformed.');
  }
  return identity;
}

async function withDeadline(label, operation, timeoutMs = budgets.operationMs) {
  let timer;
  try {
    return await Promise.race([
      Promise.resolve().then(operation),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new DeadlineFailure(label, timeoutMs)), timeoutMs);
      })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

function errorRecord(error) {
  return {
    classification: error?.classification || 'unexpected-error',
    message: error?.message || String(error),
    details: error?.details || {},
    stack: error?.stack || null
  };
}

async function validateInputs(webRoot, output) {
  const requiredPublishFiles = [
    'index.html',
    'indexedDB.js',
    'appsettings.json',
    '_framework/blazor.webassembly.js'
  ];
  const rootStats = await stat(webRoot).catch(() => null);
  if (!rootStats?.isDirectory()) {
    throw new TruthFailure('missing-publish-root', `Web publish root does not exist: ${webRoot}`);
  }
  for (const relativePath of requiredPublishFiles) {
    const filePath = path.join(webRoot, relativePath);
    const fileStats = await stat(filePath).catch(() => null);
    if (!fileStats?.isFile()) {
      throw new TruthFailure(
        'invalid-publish-root',
        `Required extracted publish file is missing: ${relativePath}`,
        { webRoot, relativePath });
    }
  }
  for (const fixturePath of Object.values(fixtures)) {
    const fixtureStats = await stat(fixturePath).catch(() => null);
    if (!fixtureStats?.isFile()) {
      throw new TruthFailure('missing-repository-fixture', `Required fixture is missing: ${fixturePath}`);
    }
  }
  const outputParent = path.dirname(output);
  await access(outputParent).catch(() => {
    throw new TruthFailure('invalid-output-path', `Output directory does not exist: ${outputParent}`);
  });
  if (output === webRoot || output.startsWith(`${webRoot}${path.sep}`)) {
    throw new TruthFailure('invalid-output-path', 'Output must be outside the extracted publish root');
  }

  const appSettings = JSON.parse(await readFile(path.join(webRoot, 'appsettings.json'), 'utf8'));
  if (appSettings?.ProcurementRoutes?.GenerationEnabled !== false) {
    throw new TruthFailure(
      'publish-claim-mismatch',
      'Publish does not declare ProcurementRoutes:GenerationEnabled=false',
      { actual: appSettings?.ProcurementRoutes?.GenerationEnabled ?? null });
  }
  const indexedDbBytes = await readFile(path.join(webRoot, 'indexedDB.js'));
  return {
    appSettings,
    indexedDbSha256: createHash('sha256').update(indexedDbBytes).digest('hex')
  };
}

const mimeTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.dat', 'application/octet-stream'],
  ['.dll', 'application/octet-stream'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.pdb', 'application/octet-stream'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.wasm', 'application/wasm'],
  ['.woff', 'font/woff'],
  ['.woff2', 'font/woff2']
]);

const harnessPages = new Map([
  ['/__truth/blank.html', '<!doctype html><meta charset="utf-8"><title>truth seed</title>'],
  ['/__truth/indexeddb.html', '<!doctype html><meta charset="utf-8"><title>truth IndexedDB</title><script src="/indexedDB.js"></script>']
]);

async function startStaticServer(webRoot) {
  const serverErrors = [];
  const server = http.createServer(async (request, response) => {
    try {
      const requestUrl = new URL(request.url || '/', 'http://127.0.0.1');
      const pathname = decodeURIComponent(requestUrl.pathname);
      if (harnessPages.has(pathname)) {
        response.writeHead(200, {
          'content-type': 'text/html; charset=utf-8',
          'cache-control': 'no-store'
        });
        response.end(harnessPages.get(pathname));
        return;
      }

      const relativePath = pathname.replace(/^\/+/, '') || 'index.html';
      let filePath = path.resolve(webRoot, relativePath);
      if (filePath !== webRoot && !filePath.startsWith(`${webRoot}${path.sep}`)) {
        response.writeHead(403).end();
        return;
      }
      let fileStats = await stat(filePath).catch(() => null);
      if (fileStats?.isDirectory()) {
        filePath = path.join(filePath, 'index.html');
        fileStats = await stat(filePath).catch(() => null);
      }
      if (!fileStats?.isFile() && !path.extname(relativePath)) {
        filePath = path.join(webRoot, 'index.html');
        fileStats = await stat(filePath).catch(() => null);
      }
      if (!fileStats?.isFile()) {
        response.writeHead(404, { 'cache-control': 'no-store' }).end();
        return;
      }
      const bytes = await readFile(filePath);
      response.writeHead(200, {
        'content-type': mimeTypes.get(path.extname(filePath).toLowerCase()) || 'application/octet-stream',
        'cache-control': 'no-store'
      });
      response.end(bytes);
    } catch (error) {
      serverErrors.push(errorRecord(error));
      response.writeHead(500, { 'cache-control': 'no-store' }).end();
    }
  });
  await withDeadline('start static publish server', () => new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  }));
  const address = server.address();
  return {
    server,
    serverErrors,
    origin: `http://127.0.0.1:${address.port}`
  };
}

async function closeServer(server) {
  if (!server) return;
  await withDeadline('close static publish server', () => new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
  }), budgets.closeMs);
}

function createScenario(browserName, name, requiredAssertions) {
  return {
    browser: browserName,
    name,
    status: 'running',
    requiredAssertions,
    assertions: [],
    diagnostics: {
      consoleErrors: [],
      consoleWarnings: [],
      routeExecutionEvidence: [],
      pageErrors: [],
      localRequestFailures: [],
      badLocalResponses: []
    },
    network: {
      fixtureRequests: [],
      rejectedRequests: [],
      workerRequests: [],
      forwardedExternalRequests: []
    }
  };
}

function claim(scenario, name, condition, details = {}) {
  const assertion = { name, passed: Boolean(condition), details };
  scenario.assertions.push(assertion);
  if (!condition) {
    throw new TruthFailure('assertion-failed', `Assertion failed: ${name}`, details);
  }
}

function finishScenario(scenario, error) {
  const completed = new Set(scenario.assertions.map(assertion => assertion.name));
  for (const name of scenario.requiredAssertions) {
    if (!completed.has(name)) {
      scenario.assertions.push({ name, passed: false, details: { reason: 'assertion not reached' } });
    }
  }
  const failedAssertions = scenario.assertions.filter(assertion => !assertion.passed);
  scenario.status = error || failedAssertions.length > 0 ? 'failed' : 'passed';
  if (error) scenario.failure = errorRecord(error);
  scenario.assertionCount = scenario.assertions.length;
  scenario.passedAssertionCount = scenario.assertions.length - failedAssertions.length;
  return scenario;
}

function installPageDiagnostics(page, scenario, origin) {
  page.on('console', message => {
    const entry = { type: message.type(), text: message.text() };
    if (message.type() === 'error') scenario.diagnostics.consoleErrors.push(entry);
    if (message.type() === 'warning') scenario.diagnostics.consoleWarnings.push(entry);
    if (/explicit route generation starting|route execution returned|route reconciliation starting|route workflow returned/i.test(entry.text)) {
      scenario.diagnostics.routeExecutionEvidence.push(entry);
    }
  });
  page.on('pageerror', error => {
    scenario.diagnostics.pageErrors.push({ message: error.message, stack: error.stack || null });
  });
  page.on('requestfailed', request => {
    if (request.url().startsWith(origin)) {
      scenario.diagnostics.localRequestFailures.push({
        method: request.method(),
        url: request.url(),
        error: request.failure()?.errorText || null
      });
    }
  });
  page.on('response', response => {
    if (response.url().startsWith(origin) && response.status() >= 400) {
      scenario.diagnostics.badLocalResponses.push({ url: response.url(), status: response.status() });
    }
  });
}

function isWorkerRequest(url) {
  const value = `${url.hostname}${url.pathname}`.toLowerCase();
  return value.includes('workshophost') ||
    value.includes('marketmafioso') ||
    /\/api\/(?:capabilities|acquisition(?:\/|$))/.test(url.pathname.toLowerCase());
}

function marketResponse(itemId, dataCenter, networkFixture) {
  const world = networkFixture.dataCenters[dataCenter] || networkFixture.dataCenters.Aether;
  const unixSeconds = Math.floor(Date.parse(networkFixture.fixedNow) / 1000);
  const nqPrice = 80 + itemId % 29;
  const hqPrice = nqPrice + 17;
  return {
    itemID: itemId,
    dcName: dataCenter,
    lastUploadTime: unixSeconds * 1000,
    worldUploadTimes: { [world.worldId]: unixSeconds * 1000 },
    listings: [
      {
        pricePerUnit: nqPrice,
        quantity: 9999,
        worldName: world.worldName,
        dataCenterName: dataCenter,
        retainerName: `Truth NQ ${world.worldName}`,
        hq: false,
        lastReviewTime: unixSeconds
      },
      {
        pricePerUnit: hqPrice,
        quantity: 9999,
        worldName: world.worldName,
        dataCenterName: dataCenter,
        retainerName: `Truth HQ ${world.worldName}`,
        hq: true,
        lastReviewTime: unixSeconds
      }
    ],
    averagePrice: nqPrice,
    averagePriceNQ: nqPrice,
    averagePriceHQ: hqPrice,
    minPrice: nqPrice,
    minPriceNQ: nqPrice,
    minPriceHQ: hqPrice
  };
}

async function fulfillKnownFixture(route, parsedUrl, networkFixture) {
  if (parsedUrl.hostname === 'fonts.googleapis.com') {
    await route.fulfill({ status: 200, contentType: 'text/css', body: '/* deterministic empty font fixture */' });
    return 'google-font-css';
  }
  if (parsedUrl.hostname === 'fonts.gstatic.com') {
    await route.fulfill({ status: 200, contentType: 'font/woff2', body: Buffer.alloc(0) });
    return 'google-font-file';
  }
  if (parsedUrl.hostname === 'www.garlandtools.org' && parsedUrl.pathname === '/api/search.php') {
    const query = parsedUrl.searchParams.get('text') || '';
    const body = query.localeCompare(networkFixture.searchQuery, undefined, { sensitivity: 'accent' }) === 0
      ? networkFixture.searchResults
      : [];
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    return 'garland-search';
  }
  const garlandItem = parsedUrl.hostname === 'www.garlandtools.org'
    ? parsedUrl.pathname.match(/^\/db\/doc\/item\/en\/3\/(\d+)\.json$/)
    : null;
  if (garlandItem) {
    const itemId = Number(garlandItem[1]);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        item: {
          id: itemId,
          name: networkFixture.itemNames[String(itemId)] || `Fixture Item ${itemId}`,
          icon: itemId,
          craft: null,
          vendors: []
        },
        partials: []
      })
    });
    return 'garland-item';
  }
  if (parsedUrl.hostname === 'universalis.app' && parsedUrl.pathname.startsWith('/api/v2/')) {
    const segments = parsedUrl.pathname.split('/').filter(Boolean);
    const dataCenter = decodeURIComponent(segments.at(-2));
    const itemIds = segments.at(-1).split(',').map(Number).filter(Number.isSafeInteger);
    const responses = itemIds.map(itemId => marketResponse(itemId, dataCenter, networkFixture));
    const body = itemIds.length === 1
      ? responses[0]
      : { itemIDs: itemIds, items: Object.fromEntries(responses.map(item => [item.itemID, item])) };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    return 'universalis-market';
  }
  return null;
}

async function configureContext(context, scenario, origin, networkFixture, allowProductFixtures) {
  await context.addInitScript(fixedNowMs => {
    const NativeDate = Date;
    const startedAt = performance.now();
    const now = () => fixedNowMs + performance.now() - startedAt;
    class FixedDate extends NativeDate {
      constructor(...args) {
        super(...(args.length === 0 ? [now()] : args));
      }
      static now() { return now(); }
    }
    globalThis.Date = FixedDate;
  }, Date.parse(networkFixture.fixedNow));

  await context.route('**/*', async route => {
    const parsedUrl = new URL(route.request().url());
    if (isWorkerRequest(parsedUrl)) {
      scenario.network.workerRequests.push({ method: route.request().method(), url: parsedUrl.href });
    }
    if (parsedUrl.origin === origin) {
      await route.continue();
      return;
    }
    if (allowProductFixtures) {
      const fixtureName = await fulfillKnownFixture(route, parsedUrl, networkFixture);
      if (fixtureName) {
        scenario.network.fixtureRequests.push({
          method: route.request().method(),
          url: parsedUrl.href,
          fixture: fixtureName
        });
        return;
      }
    }
    scenario.network.rejectedRequests.push({ method: route.request().method(), url: parsedUrl.href });
    await route.abort('blockedbyclient');
  });
}

async function inspectDatabase(page) {
  return await withDeadline('inspect IndexedDB schema', () => page.evaluate(async () => {
    const request = indexedDB.open('FFXIVCraftArchitect');
    const database = await new Promise((resolve, reject) => {
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    const stores = Array.from(database.objectStoreNames).sort();
    const indexes = {};
    for (const storeName of stores) {
      indexes[storeName] = Array.from(database.transaction(storeName).objectStore(storeName).indexNames).sort();
    }
    const result = { version: database.version, stores, indexes };
    database.close();
    return result;
  }));
}

function diagnosticsAreClean(scenario) {
  return scenario.diagnostics.consoleErrors.length === 0 &&
    scenario.diagnostics.pageErrors.length === 0 &&
    scenario.diagnostics.localRequestFailures.length === 0 &&
    scenario.diagnostics.badLocalResponses.length === 0;
}

const currentAssertions = [
  'production-module-loaded',
  'empty-database-created-at-current-schema',
  'current-store-contract-present',
  'market-timestamp-index-present',
  'setting-sentinel-durable-after-current-reopen',
  'plan-sentinel-durable-after-current-reopen',
  'current-schema-stable-after-reload',
  'browser-diagnostics-clean'
];

async function runCurrentIndexedDbScenario(browserName, browser, origin, networkFixture) {
  const scenario = createScenario(browserName, 'indexeddb-empty-current', currentAssertions);
  let context;
  try {
    context = await withDeadline(`${browserName} create current IndexedDB context`, () => browser.newContext({
      serviceWorkers: 'block'
    }));
    await configureContext(context, scenario, origin, networkFixture, false);
    const page = await context.newPage();
    installPageDiagnostics(page, scenario, origin);
    await withDeadline(`${browserName} load production IndexedDB module`, () => page.goto(
      `${origin}/__truth/indexeddb.html`, { waitUntil: 'load' }));
    await withDeadline(`${browserName} wait for production IndexedDB API`, () => page.waitForFunction(
      () => Number.isInteger(window.IndexedDB?.schemaVersion) && Number.isInteger(window.IndexedDB?.moduleRevision)));
    const moduleIdentity = await page.evaluate(() => ({
      schemaVersion: window.IndexedDB.schemaVersion,
      moduleRevision: window.IndexedDB.moduleRevision
    }));
    scenario.moduleIdentity = moduleIdentity;
    claim(scenario, 'production-module-loaded', moduleIdentity.schemaVersion > 3 && moduleIdentity.moduleRevision > 0, moduleIdentity);

    const sentinel = {
      id: 'current-plan-sentinel',
      name: 'Current plan sentinel',
      savedAt: networkFixture.fixedNow,
      modifiedAt: networkFixture.fixedNow,
      dataCenter: 'Aether',
      projectItems: [{ id: 2, name: 'Fire Shard', quantity: 5 }],
      unrelatedPayload: { durable: true, lane: 'current' }
    };
    await withDeadline(`${browserName} initialize empty database`, () => page.evaluate(async ({ sentinel, setting }) => {
      await window.IndexedDB.saveSetting('truth.current.setting', setting);
      await window.IndexedDB.savePlan(sentinel);
    }, { sentinel, setting: { fixed: true, count: 9 } }));
    const initialSchema = await inspectDatabase(page);
    claim(scenario, 'empty-database-created-at-current-schema', initialSchema.version === moduleIdentity.schemaVersion, {
      databaseVersion: initialSchema.version,
      exportedSchemaVersion: moduleIdentity.schemaVersion
    });
    claim(scenario, 'current-store-contract-present', expectedCurrentStores.every(name => initialSchema.stores.includes(name)), {
      expected: expectedCurrentStores,
      actual: initialSchema.stores
    });
    claim(scenario, 'market-timestamp-index-present', initialSchema.indexes.marketCache?.includes('fetchedAtUnix'), {
      indexes: initialSchema.indexes.marketCache || []
    });

    await withDeadline(`${browserName} reload current schema`, () => page.reload({ waitUntil: 'load' }));
    await withDeadline(`${browserName} wait for module after current reload`, () => page.waitForFunction(
      () => window.IndexedDB?.schemaVersion > 3));
    const durable = await withDeadline(`${browserName} read current schema sentinels`, () => page.evaluate(async () => ({
      setting: await window.IndexedDB.loadSetting('truth.current.setting'),
      plan: await window.IndexedDB.loadPlan('current-plan-sentinel')
    })));
    claim(scenario, 'setting-sentinel-durable-after-current-reopen',
      JSON.stringify(durable.setting) === JSON.stringify({ fixed: true, count: 9 }), { actual: durable.setting });
    claim(scenario, 'plan-sentinel-durable-after-current-reopen',
      durable.plan?.unrelatedPayload?.durable === true && durable.plan?.projectItems?.[0]?.name === 'Fire Shard',
      { actual: durable.plan });
    const reopenedSchema = await inspectDatabase(page);
    claim(scenario, 'current-schema-stable-after-reload',
      reopenedSchema.version === initialSchema.version &&
        JSON.stringify(reopenedSchema.stores) === JSON.stringify(initialSchema.stores),
      { before: initialSchema, after: reopenedSchema });
    claim(scenario, 'browser-diagnostics-clean', diagnosticsAreClean(scenario), scenario.diagnostics);
  } catch (error) {
    finishScenario(scenario, error);
  } finally {
    if (context) {
      await withDeadline(`${browserName} close current IndexedDB context`, () => context.close(), budgets.closeMs)
        .catch(error => { scenario.cleanupFailure = errorRecord(error); scenario.status = 'failed'; });
    }
  }
  const cleanupFailed = Boolean(scenario.cleanupFailure);
  if (!scenario.assertionCount) finishScenario(scenario);
  if (cleanupFailed) scenario.status = 'failed';
  return scenario;
}

const historicalAssertions = [
  'historical-v3-fixture-seeded',
  'production-module-upgraded-historical-schema',
  'historical-plan-sentinel-survived',
  'historical-setting-sentinel-survived',
  'historical-market-record-survived',
  'historical-plan-summary-rebuilt',
  'upgraded-market-timestamp-index-present',
  'browser-diagnostics-clean'
];

async function runHistoricalIndexedDbScenario(browserName, browser, origin, networkFixture, historicalFixture) {
  const scenario = createScenario(browserName, 'indexeddb-v3-upgrade', historicalAssertions);
  let context;
  try {
    context = await withDeadline(`${browserName} create historical IndexedDB context`, () => browser.newContext({
      serviceWorkers: 'block'
    }));
    await configureContext(context, scenario, origin, networkFixture, false);
    const page = await context.newPage();
    installPageDiagnostics(page, scenario, origin);
    await withDeadline(`${browserName} load historical seed page`, () => page.goto(
      `${origin}/__truth/blank.html`, { waitUntil: 'load' }));
    const seeded = await withDeadline(`${browserName} seed historical v3 schema`, () => page.evaluate(async fixture => {
      const request = indexedDB.open('FFXIVCraftArchitect', fixture.schemaVersion);
      request.onupgradeneeded = () => {
        const database = request.result;
        for (const storeFixture of fixture.stores) {
          const store = database.createObjectStore(storeFixture.name, { keyPath: storeFixture.keyPath });
          for (const indexFixture of storeFixture.indexes) {
            store.createIndex(indexFixture.name, indexFixture.keyPath, { unique: indexFixture.unique });
          }
          for (const record of storeFixture.records) store.put(record);
        }
      };
      const database = await new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
      const result = { version: database.version, stores: Array.from(database.objectStoreNames).sort() };
      database.close();
      return result;
    }, historicalFixture));
    claim(scenario, 'historical-v3-fixture-seeded',
      seeded.version === historicalFixture.schemaVersion && seeded.stores.length === historicalFixture.stores.length,
      { fixture: historicalFixture.provenance, seeded });

    await withDeadline(`${browserName} load production module over historical schema`, () => page.goto(
      `${origin}/__truth/indexeddb.html`, { waitUntil: 'load' }));
    await withDeadline(`${browserName} wait for historical production upgrade`, () => page.waitForFunction(
      () => Number.isInteger(window.IndexedDB?.schemaVersion)));
    await withDeadline(`${browserName} trigger historical production upgrade`, () => page.evaluate(
      () => window.IndexedDB.loadSetting('truth.historical.setting')));
    const moduleIdentity = await page.evaluate(() => ({
      schemaVersion: window.IndexedDB.schemaVersion,
      moduleRevision: window.IndexedDB.moduleRevision
    }));
    const upgradedSchema = await inspectDatabase(page);
    claim(scenario, 'production-module-upgraded-historical-schema',
      upgradedSchema.version === moduleIdentity.schemaVersion && upgradedSchema.version > historicalFixture.schemaVersion,
      { historicalVersion: historicalFixture.schemaVersion, moduleIdentity, databaseVersion: upgradedSchema.version });

    const durable = await withDeadline(`${browserName} read upgraded historical sentinels`, () => page.evaluate(async () => ({
      plan: await window.IndexedDB.loadPlan('historical-plan-sentinel'),
      setting: await window.IndexedDB.loadSetting('truth.historical.setting'),
      market: await window.IndexedDB.loadMarketData('2@Aether'),
      summaries: await window.IndexedDB.loadPlanSummaries()
    })));
    claim(scenario, 'historical-plan-sentinel-survived',
      durable.plan?.unrelatedPayload?.owner === 'v3' && durable.plan?.projectItems?.[0]?.quantity === 7,
      { actual: durable.plan });
    claim(scenario, 'historical-setting-sentinel-survived',
      durable.setting?.label === 'settings sentinel' && JSON.stringify(durable.setting.sequence) === '[3,1,4]',
      { actual: durable.setting });
    claim(scenario, 'historical-market-record-survived',
      durable.market?.worlds?.Adamantoise?.sentinel === 'historical market payload' &&
        durable.market?.fetchedAtUnix === 1784635200,
      { actual: durable.market });
    claim(scenario, 'historical-plan-summary-rebuilt',
      durable.summaries.some(summary => summary.id === 'historical-plan-sentinel' && summary.itemCount === 1),
      { actual: durable.summaries });
    claim(scenario, 'upgraded-market-timestamp-index-present',
      upgradedSchema.indexes.marketCache?.includes('fetchedAtUnix'),
      { indexes: upgradedSchema.indexes.marketCache || [] });
    claim(scenario, 'browser-diagnostics-clean', diagnosticsAreClean(scenario), scenario.diagnostics);
  } catch (error) {
    finishScenario(scenario, error);
  } finally {
    if (context) {
      await withDeadline(`${browserName} close historical IndexedDB context`, () => context.close(), budgets.closeMs)
        .catch(error => { scenario.cleanupFailure = errorRecord(error); scenario.status = 'failed'; });
    }
  }
  const cleanupFailed = Boolean(scenario.cleanupFailure);
  if (!scenario.assertionCount) finishScenario(scenario);
  if (cleanupFailed) scenario.status = 'failed';
  return scenario;
}

async function requireVisible(locator, label, timeoutMs = budgets.operationMs) {
  try {
    await locator.waitFor({ state: 'visible', timeout: timeoutMs });
    return locator;
  } catch (error) {
    throw new TruthFailure('product-affordance-missing', `Required visible product affordance is missing: ${label}`, {
      label,
      cause: error.message
    });
  }
}

function mainNavigationButton(page, name) {
  return page.getByRole('group').getByRole('button', { name, exact: true });
}

async function readLifecycle(page) {
  return await withDeadline('read product lifecycle diagnostics', () => page.evaluate(async () => {
    const probe = document.querySelector('[data-benchmark-id="operation-lifecycle"]');
    const autosave = await window.IndexedDB?.loadPlan('autosave');
    const marketIntelligence = autosave?.marketIntelligenceJson
      ? JSON.parse(autosave.marketIntelligenceJson)
      : null;
    return {
      data: probe ? { ...probe.dataset } : null,
      autosave: autosave ? {
        id: autosave.id,
        name: autosave.name,
        projectItemCount: autosave.projectItems?.length || 0,
        hasPlan: Boolean(autosave.planJson),
        hasMarketIntelligence: Boolean(autosave.marketIntelligenceJson),
        marketRecommendationCount: marketIntelligence?.Recommendations?.length || 0,
        hasLegacyMarketPlans: Boolean(autosave.marketPlansJson)
      } : null
    };
  }));
}

async function waitForLifecycle(page, label, predicateSource, timeoutMs = budgets.startupMs, argument) {
  try {
    await withDeadline(label, () => page.waitForFunction(
      predicateSource, argument, { timeout: timeoutMs }), timeoutMs + 500);
  } catch (error) {
    throw new TruthFailure('lifecycle-timeout', `${label} did not reach its terminal state`, {
      timeoutMs,
      lifecycle: await readLifecycle(page),
      cause: errorRecord(error)
    });
  }
  return await readLifecycle(page);
}

const productAssertions = [
  'production-kill-switch-config-loaded',
  'native-plan-imported-through-visible-flow',
  'explicit-market-analysis-published',
  'market-analysis-durable-in-autosave',
  'acquisition-evaluation-available-with-kill-switch',
  'procurement-route-control-visibly-disabled',
  'disabled-route-action-does-not-execute',
  'name-first-item-search-returned-product-result',
  'name-first-item-selection-updated-project',
  'ordinary-navigation-remained-usable',
  'reload-restored-imported-plan',
  'reload-restored-market-analysis',
  'manual-acquisition-choice-remained-usable',
  'no-route-execution-observed-through-final-interaction',
  'no-worker-request-observed',
  'no-unexpected-external-request-observed',
  'browser-diagnostics-clean'
];

function routeExecutionEvidence(scenario) {
  return scenario.diagnostics.routeExecutionEvidence;
}

async function runProductScenario(browserName, browser, origin, networkFixture, planFixturePath) {
  const scenario = createScenario(browserName, 'production-procurement-kill-switch', productAssertions);
  let context;
  try {
    context = await withDeadline(`${browserName} create product context`, () => browser.newContext({
      serviceWorkers: 'block',
      viewport: { width: 1440, height: 1000 }
    }));
    await configureContext(context, scenario, origin, networkFixture, true);
    const page = await context.newPage();
    installPageDiagnostics(page, scenario, origin);
    page.setDefaultTimeout(budgets.operationMs);

    await withDeadline(`${browserName} start production application`, () => page.goto(
      `${origin}?benchmark-defer-route=1`,
      { waitUntil: 'domcontentloaded' }), budgets.startupMs);
    await requireVisible(mainNavigationButton(page, 'Recipe Planner'), 'Recipe Planner navigation', budgets.startupMs);
    await withDeadline(`${browserName} wait for product IndexedDB`, () => page.waitForFunction(
      () => Number.isInteger(window.IndexedDB?.schemaVersion)), budgets.startupMs);
    await withDeadline(`${browserName} enable production diagnostics`, () => page.evaluate(
      () => window.IndexedDB.saveSetting('debug.secret_tools_enabled', 'true')));
    await withDeadline(`${browserName} reload with production diagnostics`, () => page.reload(
      { waitUntil: 'domcontentloaded' }), budgets.startupMs);
    await withDeadline(`${browserName} wait for lifecycle diagnostics`, () => page.waitForSelector(
      '[data-benchmark-id="operation-lifecycle"]', { state: 'attached' }), budgets.startupMs);
    claim(scenario, 'production-kill-switch-config-loaded', true, {
      routeGenerationEnabled: false,
      source: 'published appsettings.json'
    });

    await requireVisible(page.locator('[data-benchmark-id="main-import-menu"]'), 'Import menu');
    await page.locator('[data-benchmark-id="main-import-menu"]').click();
    await requireVisible(page.locator('[data-benchmark-id="main-import-native-plan"]'), 'native plan import menu item');
    await page.locator('[data-benchmark-id="main-import-native-plan"]').click();
    await requireVisible(page.getByText('Browse for .craftplan file...', { exact: true }), 'native plan Browse control');
    try {
      await page.locator('#nativeFileInput').waitFor({ state: 'attached' });
    } catch (error) {
      throw new TruthFailure('product-affordance-missing', 'Native plan Browse control has no attached file input', {
        cause: error.message
      });
    }
    await page.locator('#nativeFileInput').setInputFiles(planFixturePath);
    const importButton = page.getByRole('dialog').getByRole('button', { name: 'Import', exact: true });
    await requireVisible(importButton, 'native plan Import button');
    await page.waitForFunction(() => {
      const dialog = document.querySelector('[role="dialog"]');
      return Array.from(dialog?.querySelectorAll('button') || [])
        .some(button => button.textContent?.trim() === 'Import' && !button.disabled);
    });
    await importButton.click();
    const imported = await waitForLifecycle(page, `${browserName} wait for imported plan`, () => {
      const data = document.querySelector('[data-benchmark-id="operation-lifecycle"]')?.dataset;
      return data && Number(data.planRootCount) === 1 && Number(data.planNodeCount) === 2 &&
        data.isBusy === 'false' && !data.currentOperation && !data.activeWorkflows;
    }, budgets.productMs);
    claim(scenario, 'native-plan-imported-through-visible-flow',
      imported.data?.planRootCount === '1' && imported.data?.planNodeCount === '2', imported);

    await mainNavigationButton(page, 'Market Analysis').click();
    const analysisButton = page.locator('[data-benchmark-id="market-analysis-run"]');
    await requireVisible(analysisButton, 'Run Analysis button');
    await page.waitForFunction(() => {
      const button = document.querySelector('[data-benchmark-id="market-analysis-run"]');
      return button && !button.disabled && button.dataset.canAnalyze === 'true' && button.dataset.isAnalyzing === 'false';
    });
    const beforeAnalysis = await readLifecycle(page);
    await analysisButton.click();
    const analyzed = await waitForLifecycle(page, `${browserName} wait for explicit market analysis`, previousVersion => {
      const data = document.querySelector('[data-benchmark-id="operation-lifecycle"]')?.dataset;
      if (!data) return false;
      const advanced = Number(data.publicationMarketVersion) > Number(previousVersion || 0);
      return advanced && Number(data.marketAnalysisCount) > 0 && data.publicationKind === 'Known' &&
        data.isBusy === 'false' && !data.currentOperation && !data.activeWorkflows;
    }, budgets.productMs, beforeAnalysis.data?.publicationMarketVersion);
    claim(scenario, 'explicit-market-analysis-published',
      Number(analyzed.data?.marketAnalysisCount) > 0 && analyzed.data?.publicationKind === 'Known', analyzed);
    claim(scenario, 'market-analysis-durable-in-autosave',
      analyzed.autosave?.id === 'autosave' && analyzed.autosave.hasPlan &&
        analyzed.autosave.hasMarketIntelligence && analyzed.autosave.marketRecommendationCount > 0 &&
        !analyzed.autosave.hasLegacyMarketPlans,
      { autosave: analyzed.autosave });

    await mainNavigationButton(page, 'Acquisition Evaluation').click();
    await requireVisible(page.getByText('Automatic choices temporarily unavailable', { exact: true }),
      'Acquisition Evaluation kill-switch notice');
    await requireVisible(page.getByText('Acquisition decisions', { exact: true }), 'Acquisition decisions ledger');
    claim(scenario, 'acquisition-evaluation-available-with-kill-switch', true, {
      notice: 'Automatic choices temporarily unavailable'
    });

    await mainNavigationButton(page, 'Procurement Plan').click();
    const routeButton = await requireVisible(page.locator('.pp-primary-action'), 'procurement route primary action');
    const routeStateBefore = await readLifecycle(page);
    const routeControl = await routeButton.evaluate(button => ({
      disabled: button.disabled,
      text: button.textContent?.trim() || ''
    }));
    const disabledMessage = 'Procurement route generation is temporarily unavailable while CA resolves a performance issue. Market Analysis and manual acquisition choices remain available.';
    await requireVisible(page.getByText(disabledMessage, { exact: true }), 'procurement disabled explanation');
    claim(scenario, 'procurement-route-control-visibly-disabled',
      routeControl.disabled && routeControl.text.includes('Route Unavailable'), routeControl);
    await routeButton.evaluate(button => button.click());
    await page.waitForTimeout(500);
    const routeStateAfter = await readLifecycle(page);
    const routeEvidence = routeExecutionEvidence(scenario);
    claim(scenario, 'disabled-route-action-does-not-execute',
      routeStateBefore.data?.routeValidity === 'None' &&
        routeStateAfter.data?.routeValidity === 'None' &&
        routeStateAfter.data?.routeHasDecision === 'false' &&
        routeStateAfter.data?.routeReconciling === 'false' &&
        routeEvidence.length === 0,
      { before: routeStateBefore.data, after: routeStateAfter.data, routeEvidence });

    await mainNavigationButton(page, 'Recipe Planner').click();
    const itemSearch = await requireVisible(page.locator('#project-item-search'), 'name-first project item search');
    await itemSearch.fill(networkFixture.searchQuery);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const searchResult = await requireVisible(page.getByRole('button', { name: /Fire Shard/ }),
      'Fire Shard name search result');
    claim(scenario, 'name-first-item-search-returned-product-result', true, {
      query: networkFixture.searchQuery,
      result: 'Fire Shard'
    });
    await searchResult.click();
    await requireVisible(page.getByText('Fire Shard', { exact: true }).last(), 'selected Fire Shard target item');
    claim(scenario, 'name-first-item-selection-updated-project', true, { selectedName: 'Fire Shard' });
    const removeFireShard = page.getByRole('button', { name: 'Remove Fire Shard from the project', exact: true });
    if (await removeFireShard.count()) await removeFireShard.click();

    await mainNavigationButton(page, 'Market Analysis').click();
    await requireVisible(page.getByText('Market Analysis', { exact: true }).first(), 'Market Analysis after navigation');
    await mainNavigationButton(page, 'Recipe Planner').click();
    await requireVisible(page.getByText('Bronze Ingot', { exact: true }).first(), 'imported plan after navigation');
    claim(scenario, 'ordinary-navigation-remained-usable', true, {
      visited: ['acquisition', 'procurement', 'planner', 'market', 'planner']
    });

    await withDeadline(`${browserName} reload product session`, () => page.reload(
      { waitUntil: 'domcontentloaded' }), budgets.productMs);
    const restored = await waitForLifecycle(page, `${browserName} wait for reload restoration`, () => {
      const data = document.querySelector('[data-benchmark-id="operation-lifecycle"]')?.dataset;
      return data && Number(data.planRootCount) === 1 && Number(data.planNodeCount) === 2 &&
        Number(data.marketAnalysisCount) > 0 && data.publicationKind === 'Known' &&
        data.isBusy === 'false' && !data.currentOperation && !data.activeWorkflows;
    }, budgets.productMs);
    await requireVisible(page.getByText('Bronze Ingot', { exact: true }).first(), 'restored imported plan');
    claim(scenario, 'reload-restored-imported-plan',
      restored.data?.planRootCount === '1' && restored.data?.planNodeCount === '2', restored);
    claim(scenario, 'reload-restored-market-analysis',
      Number(restored.data?.marketAnalysisCount) > 0 && restored.data?.publicationKind === 'Known', restored);

    await mainNavigationButton(page, 'Acquisition Evaluation').click();
    const buyNq = await requireVisible(page.getByRole('button', { name: /Buy NQ/ }).first(),
      'manual Buy NQ acquisition option');
    if (await buyNq.isDisabled()) {
      throw new TruthFailure('product-affordance-missing', 'Manual Buy NQ acquisition option is visible but disabled', {
        item: 'Bronze Ingot'
      });
    }
    await buyNq.click();
    await requireVisible(page.locator('.rp-detail-value').filter({ hasText: 'Buy NQ' }), 'manual Buy NQ selection');
    const craft = await requireVisible(page.getByRole('button', { name: /^Craft/ }).first(), 'manual Craft acquisition option');
    await craft.click();
    await requireVisible(page.locator('.rp-detail-value').filter({ hasText: /^Craft$/ }), 'manual Craft selection restoration');
    claim(scenario, 'manual-acquisition-choice-remained-usable', true, {
      itemName: 'Bronze Ingot',
      transitions: ['Craft', 'Buy NQ', 'Craft']
    });

    const finalLifecycle = await readLifecycle(page);
    const finalRouteEvidence = routeExecutionEvidence(scenario);
    claim(scenario, 'no-route-execution-observed-through-final-interaction',
      finalLifecycle.data?.routeValidity === 'None' &&
        finalLifecycle.data?.routeHasDecision === 'false' &&
        finalLifecycle.data?.routeReconciling === 'false' &&
        finalRouteEvidence.length === 0,
      { lifecycle: finalLifecycle.data, routeEvidence: finalRouteEvidence });
    claim(scenario, 'no-worker-request-observed', scenario.network.workerRequests.length === 0, {
      workerRequests: scenario.network.workerRequests
    });
    claim(scenario, 'no-unexpected-external-request-observed',
      scenario.network.rejectedRequests.length === 0 && scenario.network.forwardedExternalRequests.length === 0,
      {
        rejectedRequests: scenario.network.rejectedRequests,
        forwardedExternalRequests: scenario.network.forwardedExternalRequests,
        fixtureRequestCount: scenario.network.fixtureRequests.length
      });
    claim(scenario, 'browser-diagnostics-clean', diagnosticsAreClean(scenario), scenario.diagnostics);
    scenario.finalLifecycle = finalLifecycle;
  } catch (error) {
    finishScenario(scenario, error);
  } finally {
    if (context) {
      await withDeadline(`${browserName} close product context`, () => context.close(), budgets.closeMs)
        .catch(error => { scenario.cleanupFailure = errorRecord(error); scenario.status = 'failed'; });
    }
  }
  const cleanupFailed = Boolean(scenario.cleanupFailure);
  if (!scenario.assertionCount) finishScenario(scenario);
  if (cleanupFailed) scenario.status = 'failed';
  return scenario;
}

async function runBrowser(browserName, browserType, origin, networkFixture, historicalFixture, report) {
  let browser;
  try {
    browser = await withDeadline(`launch ${browserName}`, () => browserType.launch({ headless: true }), budgets.browserMs);
    report.runtime.browsers.push({
      name: browserName,
      revision: pinnedBrowserRevisions[browserName],
      version: browser.version()
    });
  } catch (error) {
    for (const [name, assertions] of [
      ['indexeddb-empty-current', currentAssertions],
      ['indexeddb-v3-upgrade', historicalAssertions],
      ['production-procurement-kill-switch', productAssertions]
    ]) {
      report.scenarios.push(finishScenario(createScenario(browserName, name, assertions), error));
    }
    return;
  }

  try {
    report.scenarios.push(await runCurrentIndexedDbScenario(browserName, browser, origin, networkFixture));
    report.scenarios.push(await runHistoricalIndexedDbScenario(
      browserName, browser, origin, networkFixture, historicalFixture));
    report.scenarios.push(await runProductScenario(browserName, browser, origin, networkFixture, fixtures.plan));
  } finally {
    await withDeadline(`close ${browserName}`, () => browser.close(), budgets.closeMs)
      .catch(error => report.cleanupFailures.push({ browser: browserName, ...errorRecord(error) }));
  }
}

async function writeTerminalReport(output, report) {
  const serialized = `${JSON.stringify(report, null, 2)}\n`;
  if (output) {
    const temporaryPath = `${output}.${process.pid}.tmp`;
    await writeFile(temporaryPath, serialized);
    try {
      await rename(temporaryPath, output);
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.code !== 'EPERM') throw error;
      await rm(output, { force: true });
      await rename(temporaryPath, output);
    }
  }
  process.stdout.write(serialized);
}

const report = {
  suite: 'craft-architect-browser-truth',
  version: 1,
  status: 'running',
  fixedNow: null,
  webRoot: null,
  output: null,
  budgets,
  identity: null,
  runtime: { node: process.version, playwright: null, browsers: [] },
  scenarios: [],
  blockers: [],
  cleanupFailures: []
};
let serverState;
let output;
let globalTimer;
let pinnedBrowserRevisions = {};

try {
  const options = parseArguments(process.argv.slice(2));
  output = options.output;
  await rm(output, { force: true });
  report.identity = acceptanceIdentity();
  const [packageLock, browserDefinitions] = await Promise.all([
    readFile(path.join(here, 'package-lock.json'), 'utf8').then(JSON.parse),
    readFile(path.join(here, 'node_modules/playwright-core/browsers.json'), 'utf8').then(JSON.parse)
  ]);
  report.runtime.playwright = packageLock?.packages?.['node_modules/playwright']?.version ?? null;
  pinnedBrowserRevisions = Object.fromEntries(['chromium', 'firefox'].map(name => {
    const browser = browserDefinitions?.browsers?.find(candidate => candidate.name === name);
    return [name, browser?.revision ?? null];
  }));
  report.webRoot = 'verified-extracted-publish';
  report.output = path.basename(options.output);
  const validation = await validateInputs(options.webRoot, options.output);
  report.publish = validation;
  const [networkFixture, historicalFixture] = await Promise.all([
    readFile(fixtures.network, 'utf8').then(JSON.parse),
    readFile(fixtures.historical, 'utf8').then(JSON.parse)
  ]);
  report.fixedNow = networkFixture.fixedNow;
  serverState = await startStaticServer(options.webRoot);
  report.origin = serverState.origin;

  await Promise.race([
    (async () => {
      await runBrowser('chromium', chromium, serverState.origin, networkFixture, historicalFixture, report);
      await runBrowser('firefox', firefox, serverState.origin, networkFixture, historicalFixture, report);
    })(),
    new Promise((_, reject) => {
      globalTimer = setTimeout(() => reject(new DeadlineFailure('whole browser truth suite', budgets.globalMs)), budgets.globalMs);
    })
  ]);
} catch (error) {
  report.blockers.push(errorRecord(error));
} finally {
  clearTimeout(globalTimer);
  if (serverState) {
    await closeServer(serverState.server).catch(error => report.cleanupFailures.push({
      component: 'static-server',
      ...errorRecord(error)
    }));
    if (serverState.serverErrors.length > 0) {
      report.blockers.push({
        classification: 'static-server-error',
        message: 'Static publish server reported request errors',
        details: { errors: serverState.serverErrors }
      });
    }
  }
}

for (const scenario of report.scenarios) {
  if (scenario.status !== 'passed' && scenario.failure) {
    report.blockers.push({
      browser: scenario.browser,
      scenario: scenario.name,
      ...scenario.failure
    });
  }
  if (scenario.cleanupFailure) {
    report.cleanupFailures.push({
      browser: scenario.browser,
      scenario: scenario.name,
      ...scenario.cleanupFailure
    });
  }
}
const failedScenarios = report.scenarios.filter(scenario => scenario.status !== 'passed');
report.summary = {
  scenarioCount: report.scenarios.length,
  passedScenarioCount: report.scenarios.length - failedScenarios.length,
  failedScenarioCount: failedScenarios.length,
  assertionCount: report.scenarios.reduce((total, scenario) => total + (scenario.assertionCount || 0), 0),
  passedAssertionCount: report.scenarios.reduce((total, scenario) => total + (scenario.passedAssertionCount || 0), 0)
};
report.status = report.blockers.length === 0 && report.cleanupFailures.length === 0 &&
  report.scenarios.length === 6 && failedScenarios.length === 0
  ? 'passed'
  : 'failed';

try {
  await writeTerminalReport(output, report);
} catch (error) {
  report.status = 'failed';
  report.terminalReportFailure = errorRecord(error);
  const fallback = {
    ...report,
    status: 'failed'
  };
  process.stdout.write(`${JSON.stringify(fallback, null, 2)}\n`);
}
process.exitCode = report.status === 'passed' ? 0 : 1;
