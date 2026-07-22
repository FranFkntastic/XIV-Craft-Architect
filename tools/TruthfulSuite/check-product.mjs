import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

const [root, domain] = process.argv.slice(2);
if (!root || !domain || path.isAbsolute(root) || root.split(/[\\/]/).includes('..')) {
  throw new Error('Usage: check-product.mjs <repository-relative-root> <domain>');
}

const config = JSON.parse(await readFile(path.join(root, 'appsettings.json'), 'utf8'));
assert.equal(config?.LodestoneLookup?.BaseAddress, `https://${domain}/api/`);
assert.equal(config?.ProcurementRoutes?.GenerationEnabled, false);

const index = await readFile(path.join(root, 'index.html'), 'utf8');
assert.match(index, /<html/i);
assert.match(index, /_framework\/blazor\.webassembly\.js/);

console.log(`Verified extracted product configuration for ${domain}; procurement route generation is disabled.`);
