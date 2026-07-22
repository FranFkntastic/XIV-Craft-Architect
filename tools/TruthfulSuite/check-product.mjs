import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

const [root, domain, slot] = process.argv.slice(2);
if (!root || !domain || !['main', 'local-dev'].includes(slot) ||
    path.isAbsolute(root) || root.split(/[\\/]/).includes('..')) {
  throw new Error('Usage: check-product.mjs <repository-relative-root> <domain> <main|local-dev>');
}

const config = JSON.parse(await readFile(path.join(root, 'appsettings.json'), 'utf8'));
const enabled = slot === 'local-dev';
assert.equal(config?.LodestoneLookup?.BaseAddress, `https://${domain}/api/`);
assert.equal(config?.ProcurementRoutes?.GenerationEnabled, enabled);
assert.equal(config?.EngineRewrite?.ExecutionEnabled, enabled);
assert.equal(config?.EngineAcceptance?.Enabled, false);
assert.equal(config?.EngineAcceptance?.UseDeterministicEvidence, false);

const index = await readFile(path.join(root, 'index.html'), 'utf8');
assert.match(index, /<html/i);
assert.match(index, /_framework\/blazor\.webassembly\.js/);

console.log(`Verified extracted ${slot} product configuration for ${domain}; guarded engine enabled: ${enabled}.`);
