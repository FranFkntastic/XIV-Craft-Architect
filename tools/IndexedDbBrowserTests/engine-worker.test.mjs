import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { spawn } from 'node:child_process';
import http from 'node:http';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '../..');
let publishRoot = process.env.ENGINE_WORKER_PUBLISH_ROOT
  ? path.resolve(process.env.ENGINE_WORKER_PUBLISH_ROOT)
  : null;
let temporaryPublishRoot;
let server;
let origin;

before(async () => {
  if (!publishRoot) {
    temporaryPublishRoot = await mkdtemp(path.join(tmpdir(), 'craft-architect-engine-worker-'));
    await run('dotnet', [
      'publish',
      path.join(repositoryRoot, 'src', 'FFXIV Craft Architect.Web', 'FFXIV Craft Architect.Web.csproj'),
      '-c', 'Release',
      '--no-restore',
      '-o', temporaryPublishRoot
    ], repositoryRoot);
    publishRoot = path.join(temporaryPublishRoot, 'wwwroot');
  }
  assert.equal((await stat(path.join(publishRoot, 'index.html'))).isFile(), true);
  server = http.createServer(async (request, response) => {
    try {
      const requestPath = new URL(request.url, 'http://localhost').pathname;
      const relativePath = requestPath === '/' ? 'index.html' : decodeURIComponent(requestPath.slice(1));
      const filePath = path.resolve(publishRoot, relativePath);
      if (!filePath.startsWith(`${publishRoot}${path.sep}`) && filePath !== path.join(publishRoot, 'index.html')) {
        response.writeHead(403).end();
        return;
      }
      const body = await readFile(filePath);
      response.writeHead(200, {
        'content-type': contentType(filePath),
        'cache-control': 'no-store'
      });
      response.end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
}, { timeout: 120_000 });

after(async () => {
  if (server) await new Promise(resolve => server.close(resolve));
  if (temporaryPublishRoot) await rm(temporaryPublishRoot, { recursive: true, force: true });
});

for (const [name, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${name}: managed worker executes bounded procurement and remains killable`, {
    timeout: 90_000
  }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on('console', message => {
        if (message.type() === 'error') errors.push(message.text());
      });
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(origin, { waitUntil: 'load' });

      const evidence = await page.evaluate(async () => {
        function waitFor(worker, predicate, timeoutMs, description) {
          return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
              worker.removeEventListener('message', listener);
              reject(new Error(`${description} timed out.`));
            }, timeoutMs);
            const listener = event => {
              if (!predicate(event.data)) return;
              clearTimeout(timeout);
              worker.removeEventListener('message', listener);
              resolve(event.data);
            };
            worker.addEventListener('message', listener);
          });
        }

        async function startWorker(generation) {
          const worker = new Worker('/engine-worker.js?acceptance=true', {
            type: 'module',
            name: `engine-worker-test-${generation}`
          });
          const capabilityPromise = waitFor(
            worker,
            message => message?.kind === 'capability' && message.generation === generation,
            45_000,
            'Worker capability');
          worker.postMessage({
            protocolVersion: '2', kind: 'ping', generation,
            executionId: null, transactionId: null, payload: null
          });
          return { worker, capability: await capabilityPromise };
        }

        const active = await startWorker(1);
        const progress = [];
        active.worker.addEventListener('message', event => {
          if (event.data?.kind === 'progress') progress.push(event.data);
        });
        let heartbeatCount = 0;
        let heartbeatMaxGapMs = 0;
        let previousHeartbeat = performance.now();
        const heartbeat = setInterval(() => {
          const now = performance.now();
          heartbeatMaxGapMs = Math.max(heartbeatMaxGapMs, now - previousHeartbeat);
          previousHeartbeat = now;
          heartbeatCount++;
        }, 10);
        const resultPromise = waitFor(
          active.worker,
          message => message?.kind === 'computation-result' || message?.kind === 'protocol-error',
          45_000,
          'Managed procurement result');
        active.worker.postMessage({
          protocolVersion: '2', kind: 'acceptance-execute', generation: 1,
          executionId: null, transactionId: null, payload: null
        });
        const result = await resultPromise;
        clearInterval(heartbeat);
        active.worker.terminate();

        const hanging = await startWorker(10);
        const hangStarted = waitFor(
          hanging.worker,
          message => message?.kind === 'acceptance-hang-started',
          10_000,
          'Managed hang start');
        hanging.worker.postMessage({
          protocolVersion: '2', kind: 'acceptance-hang', generation: 10,
          executionId: null, transactionId: null, payload: null
        });
        await hangStarted;
        let terminationHeartbeat = 0;
        const terminationTimer = setInterval(() => terminationHeartbeat++, 10);
        await new Promise(resolve => setTimeout(resolve, 100));
        const terminationStarted = performance.now();
        hanging.worker.terminate();
        const terminationCallMs = performance.now() - terminationStarted;
        const replacement = await startWorker(11);
        clearInterval(terminationTimer);
        replacement.worker.terminate();

        return {
          capability: active.capability,
          result,
          progress,
          heartbeatCount,
          heartbeatMaxGapMs,
          hangingCapability: hanging.capability,
          replacementCapability: replacement.capability,
          terminationHeartbeat,
          terminationCallMs
        };
      });

      assert.equal(evidence.capability.payload.dedicatedWorker, true);
      assert.equal(evidence.capability.payload.managedRuntimeReady, true);
      assert.equal(evidence.capability.payload.executionSupported, true);
      assert.equal(evidence.capability.payload.managedRuntimeAssembly, 'FFXIV_Craft_Architect.Web');
      assert.match(evidence.capability.payload.managedRuntimeProofHash, /^[0-9a-f]{64}$/i);
      assert.match(evidence.capability.payload.workerInstanceId, /^[0-9a-f-]{36}$/i);

      assert.equal(evidence.result.kind, 'computation-result');
      assert.equal(evidence.result.payload.status, 1, 'managed computation must complete');
      assert.equal(evidence.result.payload.finalPhase, 7, 'procurement must reach reconciliation');
      assert.match(evidence.result.payload.computationHash, /^[0-9a-f]{64}$/i);
      assert.equal(evidence.result.payload.computationEvidence['phase:Reconciling'], 'complete');
      const decision = evidence.result.payload.result.procurementRoute.decision;
      assert.equal(evidence.result.payload.result.procurementRoute.isComplete, true);
      assert.equal(decision.acquisitionSearchWasTruncated, false);
      assert.equal(decision.routeSearchWasTruncated, true);
      assert.equal(decision.travelSearchWasTruncated, false);
      assert.equal(decision.travelRoutesEvaluated, 0);

      assert.ok(evidence.progress.length >= 5, `received ${evidence.progress.length} progress messages`);
      assert.ok(evidence.progress.some(message =>
        message.payload.phase === 7 && /Optimizing procurement route/.test(message.payload.message)));
      assert.ok(evidence.heartbeatCount >= 2, `heartbeat advanced ${evidence.heartbeatCount} times`);
      assert.ok(evidence.heartbeatMaxGapMs < 1000, `heartbeat gap was ${evidence.heartbeatMaxGapMs}ms`);

      assert.notEqual(
        evidence.hangingCapability.payload.workerInstanceId,
        evidence.replacementCapability.payload.workerInstanceId);
      assert.equal(evidence.replacementCapability.payload.executionSupported, true);
      assert.ok(evidence.terminationHeartbeat >= 2, 'page heartbeat must advance while Worker is hung');
      assert.ok(evidence.terminationCallMs < 100, `Worker.terminate took ${evidence.terminationCallMs}ms`);
      assert.deepEqual(errors, []);
    } finally {
      await browser.close();
    }
  });
}

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, stdio: 'inherit' });
    child.once('error', reject);
    child.once('exit', code => {
      if (code === 0) resolve();
      else reject(new Error(`${command} exited with code ${code}.`));
    });
  });
}

function contentType(filePath) {
  switch (path.extname(filePath)) {
    case '.css': return 'text/css';
    case '.html': return 'text/html';
    case '.js': return 'text/javascript';
    case '.json': return 'application/json';
    case '.wasm': return 'application/wasm';
    default: return 'application/octet-stream';
  }
}
