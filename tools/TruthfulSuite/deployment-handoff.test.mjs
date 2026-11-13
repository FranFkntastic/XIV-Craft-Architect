import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const workflow = await readFile(new URL('../../.github/workflows/deploy-vps-web.yml', import.meta.url), 'utf8');
const deploy = workflow.slice(workflow.indexOf('\n  deploy:'));

test('deploy retries select the exact artifact emitted by the successful package job', () => {
  assert.match(workflow, /handoff_artifact_id: \$\{\{ steps\.handoff\.outputs\.artifact-id \}\}/);
  assert.match(workflow, /name: Upload verified release handoff\s+id: handoff\s+uses: actions\/upload-artifact@/);
  const download = deploy.slice(deploy.indexOf('- name: Download verified release handoff'), deploy.indexOf('- name: Verify release handoff'));
  assert.match(download, /artifact-ids: \$\{\{ needs\.package\.outputs\.handoff_artifact_id \}\}/);
  assert.match(download, /merge-multiple: true/);
  assert.doesNotMatch(download, /github\.run_attempt|name: web-release-/);
  assert.match(download, /path: accepted/);
});

test('a missing artifact identity fails before download can select all artifacts', () => {
  const guard = deploy.indexOf('- name: Require exact package handoff identity');
  assert.ok(guard >= 0 && guard < deploy.indexOf('- name: Download verified release handoff'));
  assert.match(deploy, /HANDOFF_ARTIFACT_ID: \$\{\{ needs\.package\.outputs\.handoff_artifact_id \}\}/);
  assert.ok(deploy.includes('[[ "$HANDOFF_ARTIFACT_ID" =~ ^[1-9][0-9]*$ ]]'));
});

test('retry after activation verifies identical release bytes before reusing them', () => {
  const activation = deploy.slice(deploy.indexOf('- name: Verify, extract, and activate accepted release'));
  const compare = activation.indexOf('sudo diff --no-dereference -qr "$staging_dir" "$release_dir"');
  const activate = activation.indexOf('sudo ln -sfn "$release_dir" "$root/current"');
  assert.ok(compare >= 0 && compare < activate);
  assert.ok(activation.indexOf('sha256sum --check --strict -') < compare);
  assert.ok(activation.includes('sudo test ! -L "$release_dir"'));
  assert.ok(activation.includes('set -euo pipefail'));
  assert.doesNotMatch(activation, /rm -rf "\$release_dir"/);
});
