import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import selenium from 'selenium-webdriver';
import firefox from 'selenium-webdriver/firefox.js';

const { Browser, Builder, By } = selenium;

const url = new URL(process.argv[2] ?? 'https://dev.xivcraftarchitect.com/');
const planPath = path.resolve(
  process.argv[3] ?? 'C:/Users/gianf/Downloads/crasher.craftplan');
const outputPath = path.resolve(
  process.argv[4] ?? 'stock-firefox-acceptance.json');
const screenshotPath = /\.json$/i.test(outputPath)
  ? outputPath.replace(/\.json$/i, '.png')
  : `${outputPath}.png`;
const firefoxBinary = path.resolve(
  process.env.CA_STOCK_FIREFOX_BINARY ??
    'C:/Program Files/Mozilla Firefox/firefox.exe');
const headless = process.env.CA_STOCK_FIREFOX_HEADLESS === '1';
const bidi = process.env.CA_STOCK_FIREFOX_BIDI === '1';
const autoAnalysisOnly =
  process.env.CA_STOCK_FIREFOX_AUTO_ANALYSIS_ONLY === '1';
const startedAt = performance.now();
const elapsed = () => Math.round(performance.now() - startedAt);
const report = {
  browser: 'Mozilla Firefox',
  binary: firefoxBinary,
  url: url.href,
  plan: planPath,
  headless,
  bidi,
  autoAnalysisOnly,
  startedAt: new Date().toISOString(),
  stages: [],
  console: [],
  javascriptErrors: [],
  responses: [],
  requestFailures: []
};

let driver;
let consoleHandler;
let javascriptErrorHandler;

function stage(name, details = {}) {
  report.stages.push({ name, atMs: elapsed(), ...details });
}

async function waitFor(label, predicate, timeoutMs = 120_000, intervalMs = 250) {
  const deadline = performance.now() + timeoutMs;
  let lastError;
  while (performance.now() < deadline) {
    try {
      const value = await predicate();
      if (value) {
        return value;
      }
    } catch (error) {
      lastError = error;
    }
    await new Promise(resolve => setTimeout(resolve, intervalMs));
  }

  const suffix = lastError ? ` Last error: ${lastError.message}` : '';
  throw new Error(`${label} exceeded ${timeoutMs}ms.${suffix}`);
}

async function lifecycle() {
  return await driver.executeScript(() => {
    const element = document.querySelector(
      '[data-benchmark-id="operation-lifecycle"]');
    return element ? { ...element.dataset } : null;
  });
}

async function waitForLifecycle(label, predicate, timeoutMs = 120_000) {
  return await waitFor(label, async () => {
    const snapshot = await lifecycle();
    return snapshot && predicate(snapshot) ? snapshot : null;
  }, timeoutMs);
}

async function clickCss(selector) {
  const element = await waitFor(
    `find ${selector}`,
    async () => (await driver.findElements(By.css(selector)))[0],
    30_000);
  await driver.executeScript(element => element.click(), element);
}

async function clickButton(text) {
  const element = await waitFor(
    `find interactive element ${text}`,
    async () => {
      const elements = await driver.findElements(By.css('button, a'));
      for (const element of elements) {
        if ((await element.getText()).trim().toLowerCase() ===
              text.toLowerCase() &&
            await element.isDisplayed()) {
          return element;
        }
      }
      return null;
    },
    30_000);
  await element.click();
}

async function bodyContains(text) {
  return (await driver.findElement(By.css('body')).getText()).includes(text);
}

async function bodyText() {
  return await driver.findElement(By.css('body')).getText();
}

async function findRowContaining(text, timeoutMs = 30_000) {
  return await waitFor(
    `find row containing ${text}`,
    async () => {
      const rows = await driver.findElements(By.css('tr, [role="row"]'));
      for (const row of rows) {
        if ((await row.getText()).includes(text)) {
          return row;
        }
      }
      return null;
    },
    timeoutMs);
}

async function configureDiagnostics() {
  consoleHandler = await driver.script().addConsoleMessageHandler(entry => {
    report.console.push({
      level: entry.level,
      text: entry.text,
      timestamp: entry.timeStamp
    });
  });
  javascriptErrorHandler =
    await driver.script().addJavaScriptErrorHandler(entry => {
      report.javascriptErrors.push({
        level: entry.level,
        text: entry.text,
        timestamp: entry.timeStamp,
        stackTrace: entry.stackTrace
      });
    });

  const networkModule = await import(
    'selenium-webdriver/bidi/generated/network.js');
  const network = await networkModule.Network.create(driver);
  await network.onResponseCompleted(event => {
    const response = event.response ?? {};
    report.responses.push({
      url: response.url,
      status: response.status,
      mimeType: response.mimeType
    });
  });
  await network.onFetchError(event => {
    report.requestFailures.push({
      url: event.request?.url,
      errorText: event.errorText
    });
  });
}

async function installPageDiagnostics() {
  await driver.executeScript(() => {
    if (window.__caStockFirefoxDiagnostics) {
      return;
    }
    const diagnostics = {
      console: [],
      javascriptErrors: [],
      responses: [],
      requestFailures: []
    };
    const describe = value => {
      if (value instanceof Error) {
        return value.stack || value.message;
      }
      if (typeof value === 'string') {
        return value;
      }
      try {
        return JSON.stringify(value);
      } catch {
        return String(value);
      }
    };
    for (const level of ['warn', 'error']) {
      const original = console[level].bind(console);
      console[level] = (...values) => {
        diagnostics.console.push({
          level: level === 'warn' ? 'warning' : level,
          text: values.map(describe).join(' '),
          timestamp: Date.now(),
          source: 'page'
        });
        original(...values);
      };
    }
    window.addEventListener('error', event => {
      diagnostics.javascriptErrors.push({
        level: 'error',
        text: event.message || 'Unstructured window error',
        timestamp: Date.now(),
        stackTrace: event.error?.stack,
        source: 'page'
      });
    });
    window.addEventListener('unhandledrejection', event => {
      diagnostics.javascriptErrors.push({
        level: 'error',
        text: describe(event.reason),
        timestamp: Date.now(),
        stackTrace: event.reason?.stack,
        source: 'page'
      });
    });
    const originalFetch = window.fetch.bind(window);
    window.fetch = async (...args) => {
      try {
        const response = await originalFetch(...args);
        diagnostics.responses.push({
          url: response.url,
          status: response.status,
          mimeType: response.headers.get('content-type'),
          source: 'page'
        });
        return response;
      } catch (error) {
        diagnostics.requestFailures.push({
          url: String(args[0]?.url ?? args[0] ?? ''),
          errorText: describe(error),
          source: 'page'
        });
        throw error;
      }
    };
    window.__caStockFirefoxDiagnostics = diagnostics;
  });
}

async function collectPageDiagnostics() {
  const diagnostics = await driver.executeScript(() => {
    const current = window.__caStockFirefoxDiagnostics;
    if (!current) {
      return null;
    }
    const snapshot = {
      console: current.console.splice(0),
      javascriptErrors: current.javascriptErrors.splice(0),
      responses: current.responses.splice(0),
      requestFailures: current.requestFailures.splice(0)
    };
    return snapshot;
  });
  if (!diagnostics) {
    return;
  }
  report.console.push(...diagnostics.console);
  report.javascriptErrors.push(...diagnostics.javascriptErrors);
  report.responses.push(...diagnostics.responses);
  report.requestFailures.push(...diagnostics.requestFailures);
}

async function openApplication() {
  const startupStarted = performance.now();
  await driver.get(url.href);
  await waitFor(
    'application shell',
    async () => {
      const text = await bodyText();
      return text.includes('RECIPE PLANNER') && text.includes('Ready');
    },
    120_000);
  await waitFor(
    'IndexedDB module',
    () => driver.executeScript(
      () => window.IndexedDB?.moduleRevision === 20),
    120_000);
  await waitFor(
    'initial startup overlay completion',
    async () => {
      const overlays = await driver.findElements(By.css('.startup-overlay'));
      return overlays.length === 0 || !await overlays[0].isDisplayed();
    },
    120_000);
  stage('application-ready', {
    durationMs: Math.round(performance.now() - startupStarted)
  });
}

async function enableAcceptanceDiagnostics() {
  const result = await driver.executeAsyncScript(async done => {
    try {
      await window.IndexedDB.saveSetting(
        'debug.secret_tools_enabled',
        'true');
      await window.IndexedDB.saveSetting(
        'debug.defer_automatic_route_reconciliation',
        'false');
      done({
        secretTools: await window.IndexedDB.loadSetting(
          'debug.secret_tools_enabled'),
        deferReconciliation: await window.IndexedDB.loadSetting(
          'debug.defer_automatic_route_reconciliation')
      });
    } catch (error) {
      done({ error: String(error) });
    }
  });
  if (result?.error) {
    throw new Error(`Could not enable acceptance diagnostics: ${result.error}`);
  }
  if (result?.secretTools !== 'true' ||
      result?.deferReconciliation !== 'false') {
    throw new Error(
      `Acceptance settings did not persist: ${JSON.stringify(result)}`);
  }
  await driver.navigate().refresh();
  const snapshot = await waitForLifecycle(
    'Worker bootstrap after diagnostic reload',
    () => true,
    120_000);
  await waitFor(
    'startup overlay completion',
    async () => {
      const overlays = await driver.findElements(By.css('.startup-overlay'));
      return overlays.length === 0 || !await overlays[0].isDisplayed();
    },
    120_000);
  await installPageDiagnostics();
  stage('diagnostics-enabled', {
    revision: Number(snapshot.sessionRevision)
  });
}

async function importCrasher() {
  const importStarted = performance.now();
  await clickCss('[data-benchmark-id="main-import-menu"]');
  await clickCss('[data-benchmark-id="main-import-native-plan"]');
  const input = await waitFor(
    'native plan input',
    async () => (await driver.findElements(By.css('#nativeFileInput')))[0],
    30_000);
  await input.sendKeys(planPath);
  const importButton = await waitFor(
    'enabled plan import',
    async () => {
      const dialogs = await driver.findElements(By.css('[role="dialog"]'));
      if (dialogs.length === 0) {
        return null;
      }
      const buttons = await dialogs[0].findElements(By.css('button'));
      for (const button of buttons) {
        if ((await button.getText()).trim().toLowerCase() === 'import' &&
            await button.isEnabled()) {
          return button;
        }
      }
      return null;
    },
    90_000);
  await importButton.click();
  const imported = await waitForLifecycle(
    'Crasher import and recipe expansion',
    snapshot =>
      Number(snapshot.planRootCount) === 15 &&
      Number(snapshot.planNodeCount) === 88 &&
      Number(snapshot.planEdgeCount) === 73,
    180_000);
  stage('crasher-imported', {
    durationMs: Math.round(performance.now() - importStarted),
    revision: Number(imported.sessionRevision),
    rootCount: Number(imported.planRootCount),
    nodeCount: Number(imported.planNodeCount),
    edgeCount: Number(imported.planEdgeCount)
  });
}

async function runMarketAnalysis() {
  const marketStarted = performance.now();
  const candidates = await waitForLifecycle(
    'market candidate projection',
    snapshot => Number(snapshot.marketCandidateCount) === 53,
    120_000);
  if (!autoAnalysisOnly) {
    await clickCss('[data-benchmark-id="main-nav-market-analysis"]');
  }
  if (!autoAnalysisOnly && Number(candidates.marketAnalysisCount) < 53) {
    const analysisButton = await waitFor(
      'enabled market analysis action',
      async () => {
        const buttons = await driver.findElements(By.css(
          '[data-benchmark-id="market-analysis-run"]'));
        if (buttons.length === 0) {
          return null;
        }
        const button = buttons[0];
        return await button.isEnabled() ? button : null;
      },
      120_000);
    await analysisButton.click();
  }
  const analyzed = await waitForLifecycle(
    'live market analysis publication',
    snapshot =>
      Number(snapshot.marketAnalysisCount) === 53 &&
      snapshot.isBusy === 'false' &&
      !snapshot.activeWorkflows,
    autoAnalysisOnly ? 600_000 : 240_000);
  if (autoAnalysisOnly) {
    await clickCss('[data-benchmark-id="main-nav-market-analysis"]');
  }
  const lensRow = await findRowContaining('Clear Glass Lens', 60_000);
  report.clearGlassLensMarketRow = await lensRow.getText();
  stage('market-analysis-ready', {
    durationMs: Math.round(performance.now() - marketStarted),
    revision: Number(analyzed.sessionRevision),
    marketAnalysisCount: Number(analyzed.marketAnalysisCount),
    candidateCount: Number(analyzed.marketCandidateCount)
  });
}

async function verifyAcquisitionAuthority() {
  await clickButton('Acquisition Evaluation');
  const lensRow = await findRowContaining('Clear Glass Lens', 60_000);
  const rowText = await lensRow.getText();
  const source = await lensRow.findElement(By.css('select'))
    .getAttribute('value');
  const gilValues = [...rowText.matchAll(/([\d,]+)g/g)]
    .map(match => Number(match[1].replaceAll(',', '')));
  const total = gilValues.at(-1);
  const vendorTotal = 38_160;
  const isAuthoritativeChoice =
    (source === 'VendorBuy' && total === vendorTotal) ||
    (source === 'MarketBuyNq' && Number.isFinite(total) &&
      total <= vendorTotal);
  if (!isAuthoritativeChoice) {
    throw new Error(
      `Clear Glass Lens did not choose the cheaper authoritative source ` +
      `(source=${source}, total=${total}): ${rowText}`);
  }
  report.clearGlassLensAcquisition = { source, total, vendorTotal, rowText };
  stage('acquisition-authority-verified', { source, total, vendorTotal });
}

async function runProcurement() {
  await clickButton('Procurement Plan');
  const procurementStarted = performance.now();
  if (!await bodyContains('selected total')) {
    const generate = await waitFor(
      'enabled route generation action',
      async () => {
        const buttons = await driver.findElements(By.css('button'));
        for (const button of buttons) {
          if (/^(Generate|Regenerate) Route$/i.test(
                (await button.getText()).trim()) &&
              await button.isEnabled()) {
            return button;
          }
        }
        return null;
      },
      60_000);
    await generate.click();
  }
  await waitFor(
    'published procurement route',
    () => bodyContains('selected total'),
    240_000);
  const routeLifecycle = await waitForLifecycle(
    'current procurement route',
    snapshot =>
      snapshot.routeValidity === 'Current' &&
      snapshot.routeHasDecision === 'true' &&
      snapshot.isBusy === 'false',
    120_000);
  const regional = await driver.findElement(By.css(
    'input[type="checkbox"]')).isSelected();
  if (!regional) {
    throw new Error('Regional procurement was not selected by default.');
  }
  report.routeBeforeTolerance = (await bodyText()).slice(0, 12_000);
  stage('procurement-route-ready', {
    durationMs: Math.round(performance.now() - procurementStarted),
    revision: Number(routeLifecycle.sessionRevision)
  });
}

async function selectPrecomputedTolerance() {
  const before = await lifecycle();
  const selectionStarted = performance.now();
  const consoleStart = report.console.length;
  const slider = await waitFor(
    'travel tolerance slider',
    async () => (await driver.findElements(By.css('input[type="range"]')))[0],
    30_000);
  await driver.executeScript(element => {
    element.value = '6';
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, slider);
  const selected = await waitForLifecycle(
    'precomputed tolerance successor revision',
    snapshot =>
      Number(snapshot.sessionRevision) ===
        Number(before.sessionRevision) + 1 &&
      snapshot.routeValidity === 'Current' &&
      snapshot.isBusy === 'false',
    60_000);
  await waitFor(
    'position-6 route rendering',
    () => bodyContains('Position 6 of 11'),
    30_000);
  const selectionLogs = report.console.slice(consoleStart);
  if (selectionLogs.some(entry =>
      /procurement world-data=|mutate-procurement.*workflow=/i.test(
        entry.text ?? ''))) {
    throw new Error(
      'Selecting a precomputed tolerance reran procurement generation.');
  }
  const selectionDurationMs = Math.round(
    performance.now() - selectionStarted);
  if (selectionDurationMs > 10_000) {
    throw new Error(
      `Selecting a precomputed tolerance took ${selectionDurationMs}ms, ` +
      'which is inconsistent with a bounded frontier selection.');
  }
  stage('precomputed-tolerance-selected', {
    durationMs: selectionDurationMs,
    previousRevision: Number(before.sessionRevision),
    revision: Number(selected.sessionRevision),
    target: 'Position 6 via travel tolerance slider'
  });
}

async function verifyReloadPersistence() {
  const reloadStarted = performance.now();
  await collectPageDiagnostics();
  await driver.navigate().refresh();
  await waitFor(
    'restored procurement route',
    async () =>
      await bodyContains('selected total') &&
      await bodyContains('Position 6 of 11'),
    180_000);
  const restored = await waitForLifecycle(
    'restored current route lifecycle',
    snapshot =>
      snapshot.routeValidity === 'Current' &&
      snapshot.routeHasDecision === 'true' &&
      snapshot.isBusy === 'false',
    120_000);
  const regional = await driver.findElement(By.css(
    'input[type="checkbox"]')).isSelected();
  if (!regional) {
    throw new Error('Regional procurement did not survive reload.');
  }
  await installPageDiagnostics();
  stage('reload-restored', {
    durationMs: Math.round(performance.now() - reloadStarted),
    revision: Number(restored.sessionRevision)
  });
}

async function writeEvidence() {
  await collectPageDiagnostics();
  const png = await driver.takeScreenshot();
  await writeFile(screenshotPath, Buffer.from(png, 'base64'));
  report.screenshot = screenshotPath;
  report.durationMs = elapsed();
  report.finishedAt = new Date().toISOString();
  report.release = await fetch(
    new URL('release.json', url)).then(response => response.json());
  report.rateLimitedResponses = report.responses.filter(
    response => response.status === 429);
  report.serverErrors = report.responses.filter(
    response => Number(response.status) >= 500);
  const currentErrors = report.console.filter(entry =>
    ['error', 'warning'].includes(String(entry.level).toLowerCase()));
  if (report.javascriptErrors.length > 0 ||
      report.requestFailures.length > 0 ||
      report.rateLimitedResponses.length > 0 ||
      report.serverErrors.length > 0 ||
      currentErrors.length > 0) {
    throw new Error(
      'Stock Firefox completed the workflow with browser or network errors.');
  }
  report.status = 'complete';
}

try {
  const options = new firefox.Options()
    .setBinary(firefoxBinary)
    .windowSize({ width: 1600, height: 1000 })
    .setPreference('browser.shell.checkDefaultBrowser', false)
    .setPreference('browser.startup.homepage_override.mstone', 'ignore')
    .setPreference('dom.webnotifications.enabled', false);
  if (bidi) {
    options.enableBidi();
  }
  if (headless) {
    options.addArguments('-headless');
  }
  driver = await new Builder()
    .forBrowser(Browser.FIREFOX)
    .setFirefoxOptions(options)
    .build();
  const capabilities = await driver.getCapabilities();
  report.browserVersion = capabilities.get('browserVersion');
  report.platformName = capabilities.get('platformName');
  report.profile = capabilities.get('moz:profile');
  if (bidi) {
    await configureDiagnostics();
  }
  await openApplication();
  await enableAcceptanceDiagnostics();
  await importCrasher();
  await runMarketAnalysis();
  await verifyAcquisitionAuthority();
  await runProcurement();
  await selectPrecomputedTolerance();
  await verifyReloadPersistence();
  await writeEvidence();
} catch (error) {
  report.status = 'failed';
  report.error = error.stack ?? String(error);
  report.durationMs = elapsed();
  report.finishedAt = new Date().toISOString();
  if (driver) {
    try {
      await collectPageDiagnostics().catch(() => {});
      const png = await driver.takeScreenshot();
      await writeFile(screenshotPath, Buffer.from(png, 'base64'));
      report.screenshot = screenshotPath;
      report.finalBody = (await bodyText()).slice(0, 16_000);
    } catch {
      // Preserve the primary failure when the browser is already unavailable.
    }
  }
} finally {
  if (driver) {
    try {
      if (consoleHandler !== undefined) {
        await driver.script().removeConsoleMessageHandler(consoleHandler);
      }
      if (javascriptErrorHandler !== undefined) {
        await driver.script().removeJavaScriptErrorHandler(
          javascriptErrorHandler);
      }
    } catch {
      // Browser shutdown remains best-effort after evidence is captured.
    }
    await driver.quit().catch(() => {});
  }
  await writeFile(outputPath, JSON.stringify(report, null, 2));
}

if (report.status !== 'complete') {
  console.error(report.error ?? 'Stock Firefox acceptance failed.');
  process.exitCode = 1;
} else {
  console.log(JSON.stringify({
    status: report.status,
    browser: `${report.browser} ${report.browserVersion}`,
    release: report.release?.release,
    durationMs: report.durationMs,
    stages: report.stages,
    screenshot: report.screenshot
  }, null, 2));
}
