import { chromium, firefox } from 'playwright';
import { writeFile } from 'node:fs/promises';

const browserName = process.argv[2];
const url = process.argv[3] ?? 'http://127.0.0.1:5083';
const plan = process.argv[4] ?? 'C:/Users/gianf/Downloads/crasher.craftplan';
const output = process.argv[5] ?? `crasher-${browserName}.json`;
const browserType = { chromium, firefox }[browserName];
if (!browserType) throw new Error(`Unsupported browser ${browserName}`);

const started = performance.now();
const report = { browserName, url, plan, stages: [], console: [], pageErrors: [], startedAt: new Date().toISOString() };
const browser = await browserType.launch({ headless: true });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  page.on('console', message => report.console.push({ type: message.type(), text: message.text(), atMs: performance.now() - started }));
  page.on('pageerror', error => report.pageErrors.push({ message: error.message, stack: error.stack, atMs: performance.now() - started }));
  const stage = (name, details = {}) => report.stages.push({ name, atMs: Math.round(performance.now() - started), ...details });

  await page.goto(url, { waitUntil: 'networkidle', timeout: 120_000 });
  await page.waitForFunction(() => window.IndexedDB?.moduleRevision === 10, null, { timeout: 30_000 });
  stage('app-ready', { moduleRevision: await page.evaluate(() => window.IndexedDB.moduleRevision) });
  await page.evaluate(() => window.IndexedDB.saveSetting('debug.secret_tools_enabled', 'true'));
  await page.reload({ waitUntil: 'networkidle', timeout: 120_000 });
  await page.locator('[data-benchmark-id="main-import-menu"]').click();
  await page.locator('[data-benchmark-id="main-import-native-plan"]').click();
  await page.locator('#nativeFileInput').setInputFiles(plan);
  const importButton = page.getByRole('dialog').getByRole('button', { name: 'Import', exact: true });
  await importButton.waitFor({ state: 'visible' });
  await page.waitForFunction(() => {
    const dialog = document.querySelector('[role="dialog"]');
    return Array.from(dialog?.querySelectorAll('button') || [])
      .some(button => (button.textContent || '').trim().toLowerCase() === 'import' && !button.disabled);
  }, null, { timeout: 120_000 });
  await importButton.click();
  stage('plan-imported');
  await page.locator('[data-benchmark-id="main-nav-market-analysis"]').click();
  stage('automatic-analysis-observed');

  const markerStages = new Map([
    ['EnsurePopulatedAsync COMPLETE', 'cache-fetch-complete'],
    ['hot-state publication applied', 'analysis-publication-complete'],
    ['autosave complete', 'autosave-complete'],
    ['route reconciliation starting', 'route-generation-started'],
    ['route workflow returned', 'route-generation-complete']
  ]);
  const seenMarkers = new Set();
  let previous = '';
  const deadline = Date.now() + 10 * 60_000;
  while (Date.now() < deadline) {
    for (const [marker, name] of markerStages) {
      const entry = report.console.find(item => item.text.includes(marker));
      if (entry && !seenMarkers.has(marker)) {
        seenMarkers.add(marker);
        report.stages.push({ name, atMs: Math.round(entry.atMs) });
      }
    }
    const snapshot = await page.evaluate(() => {
      const text = document.body?.innerText || '';
      const statuses = Array.from(document.querySelectorAll('[role="status"], .mud-alert-message, .mud-progress-linear'))
        .map(element => (element.textContent || '').trim()).filter(Boolean);
      const visibleBusy = Array.from(document.querySelectorAll('.mud-progress-linear, .mud-progress-circular'))
        .some(element => { const rect = element.getBoundingClientRect(); return rect.width > 0 && rect.height > 0; });
      const run = document.querySelector('[data-benchmark-id="market-analysis-run"]');
      return { text, statuses, rows: document.querySelectorAll('table tr').length, visibleBusy, runEnabled: Boolean(run && !run.disabled) };
    });
    const status = snapshot.statuses.join(' | ');
    if (status !== previous) {
      stage('status', { status });
      previous = status;
    }
    const routeComplete = seenMarkers.has('route workflow returned');
    if (routeComplete && !/\bANALYZING\b/i.test(snapshot.text) && !snapshot.visibleBusy && snapshot.runEnabled) {
      await page.waitForTimeout(2000);
      stage('ui-settled', { rows: snapshot.rows, status });
      report.completed = true;
      break;
    }
    if (/suspect cache|failed|could not|unhandled exception/i.test(status)) {
      throw new Error(`Analysis hard failure: ${status}`);
    }
    await page.waitForTimeout(250);
  }
  if (!report.completed) throw new Error('Timed out waiting for the full analysis, route, persistence, and render transaction');

  const interactionStarted = performance.now();
  await page.getByRole('button', { name: 'Procurement Plan', exact: true }).click();
  await page.locator('#procurement-route-title').waitFor({ state: 'visible', timeout: 30_000 });
  await page.waitForFunction(() => !/Generating route|Updating Route/i.test(document.body?.innerText || ''), null, { timeout: 30_000 });
  stage('procurement-route-visible', { responseMs: Math.round(performance.now() - interactionStarted) });
  await page.getByRole('button', { name: 'Recipe Planner', exact: true }).click();
  await page.getByRole('button', { name: 'Procurement Plan', exact: true }).click();
  await page.locator('#procurement-route-title').waitFor({ state: 'visible', timeout: 30_000 });
  stage('post-completion-navigation-complete');

  await page.reload({ waitUntil: 'networkidle', timeout: 120_000 });
  await page.getByRole('button', { name: 'Procurement Plan', exact: true }).click();
  await page.locator('#procurement-route-title').waitFor({ state: 'visible', timeout: 60_000 });
  stage('autosave-restored-after-reload');
  report.finalBodyPreview = (await page.locator('body').innerText()).slice(0, 4000);
  report.durationMs = Math.round(performance.now() - started);
  report.finishedAt = new Date().toISOString();
} catch (error) {
  report.error = error.stack || String(error);
  report.durationMs = Math.round(performance.now() - started);
} finally {
  await browser.close();
  await writeFile(output, JSON.stringify(report, null, 2));
}
if (report.error) {
  console.error(report.error);
  process.exitCode = 1;
} else {
  console.log(JSON.stringify({ browserName, durationMs: report.durationMs, stages: report.stages, pageErrors: report.pageErrors }, null, 2));
}
