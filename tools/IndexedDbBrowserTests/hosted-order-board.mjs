import { readFile, stat, writeFile } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((items, value, index, values) => {
  if (value.startsWith('--')) items.push([value.slice(2), values[index + 1]]);
  return items;
}, []));
const webRoot = path.resolve(args['web-root'] || '');
const output = path.resolve(args.output || path.join(here, 'hosted-order-board-result.json'));
const screenshot = path.resolve(args.screenshot || path.join(here, 'hosted-order-board.png'));
if (!(await stat(path.join(webRoot, 'index.html')).catch(() => null))?.isFile()) {
  throw new Error('Usage: node hosted-order-board.mjs --web-root <publish/wwwroot> [--output result.json] [--screenshot board.png]');
}

const companyId = '10000000-0000-0000-0000-000000000001';
const profileId = '20000000-0000-0000-0000-000000000001';
const fixedNow = '2026-08-17T16:00:00.000Z';
const groupNames = ['Open', 'Claim / Identity Review', 'Needs prerequisites', 'Crafting', 'Ready for delivery', 'Manual resolution'];
const mime = new Map([
  ['.css', 'text/css'], ['.dat', 'application/octet-stream'], ['.dll', 'application/octet-stream'],
  ['.html', 'text/html'], ['.js', 'text/javascript'], ['.json', 'application/json'],
  ['.png', 'image/png'], ['.svg', 'image/svg+xml'], ['.wasm', 'application/wasm'],
  ['.woff', 'font/woff'], ['.woff2', 'font/woff2']
]);

function guid(number) {
  return `30000000-0000-0000-0000-${String(number).padStart(12, '0')}`;
}

function commissionFor(index, orderId) {
  const group = index % 6;
  const commissionId = guid(1000 + index);
  const claim = group === 0 ? null : {
    claimId: guid(2000 + index), acceptedTermsVersion: 1, claimedAtUtc: fixedNow,
    crafterId: guid(3000 + index), provisionalCrafterId: null,
    accountEvidence: { profileId, discordUserId: `sock-${index}`, discordDisplayNameSnapshot: `Sock ${index}` }
  };
  const satisfied = 2;
  const pending = 1;
  const identityState = group === 1 ? pending : satisfied;
  const paymentState = group === 2 ? pending : satisfied;
  const status = [1, 2, 2, 3, 4, 7][group];
  return {
    order: {
      id: orderId, authoringSchemaVersion: 1, companyProfileId: companyId,
      title: `Fixture ${String(index + 1).padStart(3, '0')} Commission`, status,
      commissionedAtUtc: new Date(Date.parse(fixedNow) - index * 60000).toISOString(),
      createdAtUtc: fixedNow, updatedAtUtc: fixedNow,
      sourceSnapshot: {
        sourceKind: 1, sourcePlanName: 'Hosted sync acceptance fixture', importedAtUtc: fixedNow,
        rootItems: [{ itemId: 2, name: 'Cobalt Ingot', quantity: 10, mustBeHq: false, estimatedSaleValue: 0 }],
        materials: [], craftLabor: [], requestedDataCenters: [], warnings: []
      },
      paymentSchedule: 0, history: [], syncState: 1,
      companyCommission: {
        schemaVersion: 1, commissionId, companyId, commissionerActorId: 'acceptance-commissioner',
        reference: `CA-HALFA-${String(index + 1).padStart(3, '0')}`,
        createdAtUtc: fixedNow, updatedAtUtc: fixedNow, currentTermsVersion: 1,
        termsVersions: [{
          version: 1, createdAtUtc: fixedNow,
          createdBy: { actorId: 'acceptance-commissioner', kind: 0, displayName: 'Commissioner' },
          outputs: [{ lineId: guid(4000 + index), itemId: 2, name: 'Cobalt Ingot', requiredQuantity: 10, mustBeHq: false }],
          materials: [],
          payment: { schedule: 0, contractLabel: 'Labor standard', materialReimbursement: 0, materialAdjustment: 0, craftLabor: 3000, total: 3000 },
          deliveryInstructions: 'Meet at the company workshop.',
          pricingEvidence: { costBasis: 'Fixture', marketScope: 'Aether', location: 'Adamantoise', capturedAtUtc: fixedNow },
          contactInstructions: 'Discord', changeSummary: null
        }],
        publicMetadata: { publicBriefId: `half-a-${index}`, publicUrl: `https://example.test/c/${index}`, viewState: 1, isTestFixture: false, publishedAtUtc: fixedNow, discordBindings: [] },
        activeClaimCapabilityRevision: 1, activeClaim: claim,
        manualResolution: group === 5 ? { resolutionId: guid(5000 + index), claimId: claim.claimId, previousStatus: 3, requestedAtUtc: fixedNow, requestedByActorId: 'system', reason: 'Acceptance fixture' } : null,
        participantAcknowledgedTermsVersion: claim ? 1 : null,
        gates: {
          identity: { state: identityState },
          payment: { state: paymentState, termsVersion: 1 },
          companyMaterials: { state: satisfied, promisedQuantities: [] }
        },
        outputProgress: [], deliveryReadiness: { isReady: group === 4, declaredAtUtc: group === 4 ? fixedNow : null },
        settlementState: 0, settlementPayment: { termsVersion: 1 }, activity: [], processedCommands: []
      }
    },
    commissionId
  };
}

const fixture = Array.from({ length: 100 }, (_, index) => commissionFor(index, guid(index + 1)));
const appSettings = JSON.parse(await readFile(path.join(webRoot, 'appsettings.json'), 'utf8'));
const pendingOwnerResponses = new Set();
const pendingOwners = [];
const requestLog = [];
let origin;

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url || '/', origin || 'http://127.0.0.1');
  requestLog.push(`${request.method} ${url.pathname}${url.search}`);
  if (url.pathname === '/appsettings.json') {
    const body = JSON.stringify({ ...appSettings, ProfileHost: { BaseAddress: `${origin}/api/` }, LodestoneLookup: { BaseAddress: `${origin}/api/` } });
    response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' }).end(body);
    return;
  }
  if (url.pathname === '/api/profile-host/changes') {
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ serverRevision: 100, hasMore: false, objects: [] }));
    return;
  }
  if (url.pathname === '/api/profile-host/profile') {
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ profileId, displayName: 'Half A Acceptance', metadataRevision: 1, serverRevision: 100 }));
    return;
  }
  if (url.pathname === '/api/identity/v1/signin/discord/status') {
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ configured: false, authenticated: false }));
    return;
  }
  if (url.pathname === '/api/trade/v1/memberships' || url.pathname.endsWith('/discord/publications')) {
    response.writeHead(200, { 'content-type': 'application/json' }).end('[]');
    return;
  }
  if (url.pathname === '/api/profile-host/changes/stream') {
    response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
    response.write(': acceptance stream\n\n');
    pendingOwnerResponses.add(response);
    request.on('close', () => pendingOwnerResponses.delete(response));
    return;
  }
  if (/^\/api\/trade\/v1\/companies\/[^/]+\/commissions\/[^/]+\/owner$/.test(url.pathname)) {
    const commissionId = url.pathname.split('/').at(-2);
    pendingOwners.push({ response, commissionId });
    pendingOwnerResponses.add(response);
    request.on('close', () => pendingOwnerResponses.delete(response));
    return;
  }
  let relative = url.pathname.replace(/^\/+/, '') || 'index.html';
  if (relative.startsWith('trade/') && path.extname(relative)) relative = relative.slice('trade/'.length);
  let file = path.resolve(webRoot, relative);
  let info = await stat(file).catch(() => null);
  if (!info?.isFile() && !path.extname(relative)) file = path.join(webRoot, 'index.html');
  info = await stat(file).catch(() => null);
  if (!info?.isFile() || (file !== webRoot && !file.startsWith(`${webRoot}${path.sep}`))) {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, { 'content-type': mime.get(path.extname(file)) || 'application/octet-stream', 'cache-control': 'no-store' });
  response.end(await readFile(file));
});

await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, serviceWorkers: 'block' });
const page = await context.newPage();
const diagnostics = [];
const badResponses = [];
page.on('pageerror', error => diagnostics.push(`pageerror: ${error.message}`));
page.on('response', response => {
  if (response.url().startsWith(origin) && response.status() >= 400) {
    badResponses.push(`${response.status()} ${response.request().method()} ${response.url()}`);
  }
});

try {
  await page.goto(`${origin}/indexedDB.js`, { waitUntil: 'load' });
  await page.goto(`${origin}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => window.IndexedDB?.saveTradeOrdersBatch);
  await page.evaluate(async ({ companyId, profileId, fixedNow, origin, fixture }) => {
    const hostUrl = `${origin}/api/`;
    const authority = encodeURIComponent(hostUrl.toLowerCase());
    const settings = new Map([
      ['trade.selected_company_profile_id', JSON.stringify(companyId)],
      ['trade.selected_workspace_company_id', JSON.stringify(companyId)],
      ['profileHost.hostUrl', JSON.stringify(hostUrl)],
      ['profileHost.accessKey', JSON.stringify('half-a-acceptance-key')],
      ['profileHost.rememberAccessKey', JSON.stringify(true)],
      ['profileHost.connectedProfileId', JSON.stringify(profileId)],
      ['profileHost.connectedProfileName', JSON.stringify('Half A Acceptance')],
      ['profileHost.connectedProfileMetadataRevision', JSON.stringify(1)],
      ['profileHost.authorityMigration.v1', JSON.stringify(true)],
      [`profileHost.authority.${authority}.profile.${profileId}.lastSyncRevision`, JSON.stringify(100)]
    ]);
    for (const { order } of fixture) {
      settings.set(`profileHost.authority.${authority}.profile.${profileId}.objectRevision.tradeOrders.${encodeURIComponent(order.id)}`, JSON.stringify(100));
    }
    await window.IndexedDB.saveTradeCompanyProfile({
      id: companyId, schemaVersion: 3, name: 'Half A Acceptance Company', syncState: 1,
      createdAtUtc: fixedNow, updatedAtUtc: fixedNow
    });
    await window.IndexedDB.saveTradeOrdersBatch(fixture.map(item => item.order));
    for (const [key, value] of settings) await window.IndexedDB.saveSetting(key, value);
  }, { companyId, profileId, fixedNow, origin, fixture });

  await page.goto(`${origin}/trade/orders`, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.trade-orders-rail-order');
  await page.waitForFunction(() => document.querySelectorAll('.trade-orders-rail-order').length === 100);
  const before = await page.evaluate(() => ({
    groups: [...document.querySelectorAll('.trade-orders-rail-group')].map(section => ({
      label: section.querySelector('.trade-orders-rail-group-title span')?.textContent?.trim(),
      count: Number(section.querySelector('.trade-orders-count-chip')?.textContent?.replace(/,/g, '')),
      rows: section.querySelectorAll('.trade-orders-rail-order').length
    })),
    syncGroup: [...document.querySelectorAll('.trade-orders-rail-group-title')].some(node => node.textContent.includes('Needs Attention')),
    titles: [...document.querySelectorAll('.trade-orders-rail-title')].map(node => node.textContent.trim())
  }));
  const selectedTitle = 'Fixture 050 Commission';
  await page.getByRole('button', { name: new RegExp(selectedTitle) }).click();
  await page.waitForFunction(title => document.querySelector('.trade-orders-rail-order[aria-current="true"]')?.textContent.includes(title), selectedTitle);
  const openHeader = page.locator('.trade-orders-rail-group-title').filter({ hasText: /^\s*Open\s*/ });
  await openHeader.click();
  await page.locator('.trade-orders-rail-scroll').evaluate(node => { node.scrollTop = 320; });
  const stateBeforeVerification = await page.evaluate(() => ({
    selected: document.querySelector('.trade-orders-rail-order[aria-current="true"] .trade-orders-rail-title')?.textContent?.trim(),
    openRows: [...document.querySelectorAll('.trade-orders-rail-group')].find(node => node.textContent.includes('Open'))?.querySelectorAll('.trade-orders-rail-order').length,
    scrollTop: document.querySelector('.trade-orders-rail-scroll')?.scrollTop,
    rowCount: document.querySelectorAll('.trade-orders-rail-order').length
  }));
  await page.waitForTimeout(1200);
  const after = await page.evaluate(() => ({
    selected: document.querySelector('.trade-orders-rail-order[aria-current="true"] .trade-orders-rail-title')?.textContent?.trim(),
    openRows: [...document.querySelectorAll('.trade-orders-rail-group')].find(node => node.textContent.includes('Open'))?.querySelectorAll('.trade-orders-rail-order').length,
    scrollTop: document.querySelector('.trade-orders-rail-scroll')?.scrollTop,
    rowCount: document.querySelectorAll('.trade-orders-rail-order').length,
    syncGroup: [...document.querySelectorAll('.trade-orders-rail-group-title')].some(node => node.textContent.includes('Needs Attention'))
  }));
  await page.screenshot({ path: screenshot, fullPage: true });
  const j16OwnerRequests = requestLog.filter(item => item.includes('/owner'));
  const cancelClick = page.getByRole('button', { name: 'Cancel Commission', exact: true }).click();
  await page.waitForTimeout(100);
  const releaseOwner = pending => {
    const projection = fixture.find(item => item.commissionId === pending.commissionId);
    pendingOwnerResponses.delete(pending.response);
    pending.response.writeHead(200, { 'content-type': 'application/json' });
    pending.response.end(JSON.stringify({ order: projection.order, objectRevision: 100, companyRevision: 100, profileObjectRevision: 100 }));
  };
  releaseOwner(pendingOwners.shift());
  const priorityDeadline = Date.now() + 5000;
  while (pendingOwners.length === 0 && Date.now() < priorityDeadline) await page.waitForTimeout(20);
  const priorityOwner = pendingOwners.shift();
  const selectedCommissionId = fixture[49].commissionId;
  const prioritySelectedOrder = priorityOwner?.commissionId === selectedCommissionId;
  releaseOwner(priorityOwner);
  await cancelClick;
  await page.waitForSelector('#close-order-note');
  await page.waitForTimeout(300);
  const dialogCount = await page.locator('#close-order-note').count();
  const allOwnerRequests = requestLog.filter(item => item.includes('/owner'));
  const selectedOwnerRequestCount = allOwnerRequests.filter(item => item.includes(selectedCommissionId)).length;
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  const groups = Object.fromEntries(before.groups.map(group => [group.label, group.count]));
  const assertions = {
    renderedAll100: before.titles.length === 100 && after.rowCount < 100 && after.rowCount >= 80,
    everyAttentionGroupPresent: groupNames.every(name => Number.isInteger(groups[name]) && groups[name] > 0),
    noSyntheticSyncGroup: !before.syncGroup && !after.syncGroup,
    priorClassificationImmediate: before.groups.every(group => group.count === group.rows),
    selectionPreserved: stateBeforeVerification.selected === selectedTitle && after.selected === selectedTitle,
    collapsePreserved: stateBeforeVerification.openRows === 0 && after.openRows === 0,
    scrollPreserved: stateBeforeVerification.scrollTop > 0 && after.scrollTop > 0,
    ownerVerificationBlocked: j16OwnerRequests.length === 1,
    selectedPriorityBeforeBackgroundRemainder: prioritySelectedOrder,
    lifecycleDialogOpenedOnce: dialogCount === 1,
    selectedVerificationDeduplicated: selectedOwnerRequestCount === 1,
    diagnosticsClean: diagnostics.length === 0 && badResponses.length === 0
  };
  const apiRequests = requestLog.filter(item => item.includes('/api/'));
  const result = { passed: Object.values(assertions).every(Boolean), assertions, groups, stateBeforeVerification, after, j16OwnerRequests, priorityOwner: priorityOwner?.commissionId, selectedCommissionId, selectedOwnerRequestCount, diagnostics, badResponses, apiRequests, screenshot };
  await writeFile(output, JSON.stringify(result, null, 2));
  if (!result.passed) throw new Error(`Hosted order board acceptance failed: ${JSON.stringify(result, null, 2)}`);
  process.stdout.write(`${JSON.stringify({ passed: result.passed, assertions, groups, j16OwnerRequests, priorityOwner: result.priorityOwner, selectedCommissionId, selectedOwnerRequestCount, diagnostics, badResponses, screenshot }, null, 2)}\n`);
} finally {
  for (const response of pendingOwnerResponses) response.destroy();
  await context.close();
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
