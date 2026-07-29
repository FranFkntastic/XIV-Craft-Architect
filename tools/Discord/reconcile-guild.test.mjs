import assert from 'node:assert/strict';
import test from 'node:test';
import { reconcileDiscordGuild } from './reconcile-guild.mjs';

const configuration = {
  DISCORD_BOT_TOKEN: 'token.segment.signature',
  DISCORD_APPLICATION_ID: 'app-1',
  DISCORD_GUILD_ID: 'guild-1',
  DISCORD_CHANNEL_NAME: 'craft-commissions',
  DISCORD_CHANNEL_PARENT_ID: 'category-1'
};

test('reuses the exact channel and reconciles the managed command', async () => {
  const requests = [];
  let botAttempts = 0;
  const fetchImpl = async (url, init = {}) => {
    requests.push({ url, init });
    const path = new URL(url).pathname;
    if (path.endsWith('/users/@me')) {
      botAttempts += 1;
      if (botAttempts === 1) return json({ retry_after: 0 }, 429);
      return json({ id: 'app-1', bot: true });
    }
    if (path.endsWith('/users/@me/guilds')) {
      return json([{ id: 'guild-1', name: 'Sapphire Avenue', permissions: '19472' }]);
    }
    if (path.endsWith('/guilds/guild-1/channels')) {
      return json([{
        id: 'channel-1',
        name: 'craft-commissions',
        parent_id: 'category-1',
        type: 0
      }]);
    }
    if (path.endsWith('/applications/app-1/guilds/guild-1/commands')) {
      return json([{ id: 'command-1', name: 'commission' }]);
    }
    throw new Error(`Unexpected request: ${url}`);
  };

  const result = await reconcileDiscordGuild(configuration, fetchImpl);

  assert.equal(result.channelId, 'channel-1');
  assert.equal(result.commandId, 'command-1');
  assert.equal(botAttempts, 2);
  assert.equal(
    requests.filter(request => request.init.method === 'POST').length,
    0);
  assert.equal(
    requests.find(request => request.init.method === 'PUT').init.headers.authorization,
    'Bot token.segment.signature');
});

test('creates the missing channel before publishing the command', async () => {
  const methods = [];
  const fetchImpl = async (url, init = {}) => {
    methods.push(init.method ?? 'GET');
    const path = new URL(url).pathname;
    if (path.endsWith('/users/@me')) return json({ id: 'app-1', bot: true });
    if (path.endsWith('/users/@me/guilds')) {
      return json([{ id: 'guild-1', name: 'Sapphire Avenue', permissions: '8' }]);
    }
    if (path.endsWith('/guilds/guild-1/channels') && !init.method) return json([]);
    if (path.endsWith('/guilds/guild-1/channels') && init.method === 'POST') {
      return json({
        id: 'channel-2',
        name: 'craft-commissions',
        parent_id: 'category-1',
        type: 0
      }, 201);
    }
    if (path.endsWith('/applications/app-1/guilds/guild-1/commands')) {
      return json([{ id: 'command-2', name: 'commission' }]);
    }
    throw new Error(`Unexpected request: ${url}`);
  };

  const result = await reconcileDiscordGuild(configuration, fetchImpl);

  assert.equal(result.channelId, 'channel-2');
  assert.deepEqual(methods, ['GET', 'GET', 'GET', 'POST', 'PUT']);
});

function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json' }
  });
}
