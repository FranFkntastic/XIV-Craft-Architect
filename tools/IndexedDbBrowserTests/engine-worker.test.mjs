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
  test(`${name}: managed engine worker boots without enabling execution`, {
    timeout: 60_000
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

      const result = await page.evaluate(async () => {
        async function runProbe(generation) {
          const worker = new Worker('/engine-worker.js', {
            type: 'module',
            name: `engine-worker-test-${generation}`
          });
          try {
            const messages = [];
            worker.addEventListener('message', event => messages.push(event.data));
            worker.postMessage({
              protocolVersion: '2', kind: 'ping', generation,
              executionId: null, transactionId: null, payload: null
            });
            const capability = await new Promise((resolve, reject) => {
              const timeout = setTimeout(() => reject(new Error('Worker capability timed out.')), 45_000);
              worker.addEventListener('message', event => {
                if (event.data?.kind === 'capability') {
                  clearTimeout(timeout);
                  resolve(event.data);
                }
              });
            });
            const executionId = crypto.randomUUID();
            const transactionId = crypto.randomUUID();
            async function reject(kind) {
              const response = new Promise((resolve, rejectPromise) => {
                const timeout = setTimeout(() => rejectPromise(new Error(`Worker ${kind} rejection timed out.`)), 5_000);
                const listener = event => {
                  if (event.data?.kind === 'protocol-error' &&
                      event.data.executionId === executionId &&
                      event.data.transactionId === transactionId) {
                    clearTimeout(timeout);
                    worker.removeEventListener('message', listener);
                    resolve(event.data);
                  }
                };
                worker.addEventListener('message', listener);
              });
              worker.postMessage({
                protocolVersion: '2', kind, generation,
                executionId, transactionId, payload: {}
              });
              return response;
            }
            return {
              capability,
              executeRejection: await reject('execute'),
              cancelRejection: await reject('cancel'),
              executionId,
              transactionId,
              messageCount: messages.length
            };
          } finally {
            worker.terminate();
          }
        }
        async function runConcurrentPingProbe() {
          const worker = new Worker('/engine-worker.js', { type: 'module' });
          try {
            const capabilities = [];
            worker.addEventListener('message', event => {
              if (event.data?.kind === 'capability') capabilities.push(event.data.generation);
            });
            for (const generation of [10, 11]) {
              worker.postMessage({
                protocolVersion: '2', kind: 'ping', generation,
                executionId: null, transactionId: null, payload: null
              });
            }
            const timeoutAt = performance.now() + 45_000;
            while (capabilities.length === 0 && performance.now() < timeoutAt) {
              await new Promise(resolve => setTimeout(resolve, 10));
            }
            await new Promise(resolve => setTimeout(resolve, 100));
            return capabilities;
          } finally {
            worker.terminate();
          }
        }
        return {
          first: await runProbe(1),
          replacement: await runProbe(2),
          concurrentPingGenerations: await runConcurrentPingProbe()
        };
      });

      for (const [generation, probe] of [[1, result.first], [2, result.replacement]]) {
        assert.equal(probe.capability.generation, generation);
        assert.equal(probe.capability.payload.dedicatedWorker, true);
        assert.equal(probe.capability.payload.managedRuntimeReady, true);
        assert.equal(probe.capability.payload.executionSupported, false);
        assert.equal(probe.capability.payload.managedRuntimeAssembly, 'FFXIV_Craft_Architect.Web');
        assert.match(probe.capability.payload.managedRuntimeProofHash, /^[0-9a-f]{64}$/i);
        for (const rejection of [probe.executeRejection, probe.cancelRejection]) {
          assert.equal(rejection.generation, generation);
          assert.equal(rejection.executionId, probe.executionId);
          assert.equal(rejection.transactionId, probe.transactionId);
          assert.equal(rejection.payload.code, 'engine-execution-not-enabled');
        }
        assert.ok(probe.messageCount >= 3);
      }
      assert.deepEqual(result.concurrentPingGenerations, [11]);
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
