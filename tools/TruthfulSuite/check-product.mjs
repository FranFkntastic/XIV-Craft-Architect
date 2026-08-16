import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

const [root, domain, slot, profileHostDomain = domain] = process.argv.slice(2);
if (!root || !domain || !['main', 'local-dev'].includes(slot) ||
    path.isAbsolute(root) || root.split(/[\\/]/).includes('..')) {
  throw new Error('Usage: check-product.mjs <repository-relative-root> <domain> <main|local-dev> [profile-host-domain]');
}

const config = JSON.parse(await readFile(path.join(root, 'appsettings.json'), 'utf8'));
const enabled = true;
assert.equal(config?.LodestoneLookup?.BaseAddress, `https://${domain}/api/`);
assert.equal(config?.ProfileHost?.BaseAddress, `https://${profileHostDomain}/api/`);
assert.equal(config?.ProcurementRoutes?.GenerationEnabled, enabled);
assert.equal(config?.EngineRewrite?.ExecutionEnabled, enabled);
assert.equal(config?.EngineAcceptance?.Enabled, false);
assert.equal(config?.EngineAcceptance?.UseDeterministicEvidence, false);

const index = await readFile(path.join(root, 'index.html'), 'utf8');
assert.match(index, /<html/i);
assert.match(index, /_framework\/blazor\.webassembly\.js/);
assert.match(index, /property="og:title" content="FFXIV Craft Architect"/);
assert.match(index, /property="og:description"/);
assert.match(index, /name="twitter:card" content="summary"/);

const commission = await readFile(path.join(root, 'commission.html'), 'utf8');
const commissionHead = commission.slice(0, commission.indexOf('</head>'));
assert.match(commissionHead, /property="og:title" content="Company Commission/);
assert.match(commissionHead, /property="og:description"/);
assert.match(commissionHead, /name="twitter:card" content="summary"/);
assert.doesNotMatch(commissionHead, /participant/i);

console.log(`Verified extracted ${slot} product configuration and safe public-link previews for ${domain} with profile authority ${profileHostDomain}; guarded engine enabled: ${enabled}.`);
