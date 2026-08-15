import assert from 'node:assert/strict';
import test from 'node:test';

import {
  migrateLegacyWorkspaceComponents,
  reconcileLegacyWorkspaceLabels
} from '../reconcile-guild.mjs';

const applicationId = '1532122273543360592';
const botId = applicationId;

function workspaceMessage(overrides = {}) {
  return {
    id: '200',
    application_id: applicationId,
    author: { id: botId, bot: true },
    components: [
      {
        type: 1,
        components: [
          { type: 2, style: 5, label: 'View commission', url: 'https://example.test' },
          {
            type: 2,
            style: 1,
            label: 'Open my workspace',
            custom_id: 'open-workspace:ca:v1:proof'
          }
        ]
      }
    ],
    ...overrides
  };
}

test('migrates only the legacy workspace action on this application message', () => {
  const components = migrateLegacyWorkspaceComponents(
    workspaceMessage(),
    applicationId,
    botId);

  assert.equal(components[0].components[0].label, 'View commission');
  assert.equal(components[0].components[1].label, 'Open workspace');
  assert.equal(
    components[0].components[1].custom_id,
    'open-workspace:ca:v1:proof');
});

test('ignores neutral and foreign application messages', () => {
  const neutral = workspaceMessage();
  neutral.components[0].components[1].label = 'Open workspace';
  assert.equal(
    migrateLegacyWorkspaceComponents(neutral, applicationId, botId),
    null);
  assert.equal(
    migrateLegacyWorkspaceComponents(
      workspaceMessage({ author: { id: 'other', bot: true } }),
      applicationId,
      botId),
    null);
});

test('reads the configured channel and edits only matching messages', async () => {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, init });
    const payload = init?.method === 'PATCH'
      ? { id: '200' }
      : [workspaceMessage(), workspaceMessage({ id: '201', author: { id: 'other' } })];
    return new Response(JSON.stringify(payload), {
      status: 200,
      headers: { 'content-type': 'application/json' }
    });
  };

  const updated = await reconcileLegacyWorkspaceLabels(
    fetchImpl,
    'token',
    'channel',
    applicationId,
    botId);

  assert.equal(updated, 1);
  assert.equal(calls.length, 2);
  assert.match(calls[0].url, /\/channels\/channel\/messages\?limit=100$/);
  assert.match(calls[1].url, /\/channels\/channel\/messages\/200$/);
  const body = JSON.parse(calls[1].init.body);
  assert.equal(body.components[0].components[1].label, 'Open workspace');
});
