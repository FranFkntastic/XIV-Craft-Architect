import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const DiscordApi = 'https://discord.com/api/v10';
const Administrator = 1n << 3n;
const ManageRoles = 1n << 28n;
const RequiredPermissions =
  (1n << 4n) | // Manage Channels
  (1n << 10n) | // View Channels
  (1n << 11n) | // Send Messages
  (1n << 14n); // Embed Links
const LegacyWorkspaceLabel = 'Open my workspace';
const WorkspaceLabel = 'Open workspace';
const WorkspaceActionPrefix = 'open-workspace:ca:v1:';
const MessagePageLimit = 10;

export function requireValue(configuration, name) {
  const value = configuration[name]?.trim();
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

export async function discordRequest(fetchImpl, token, path, init = {}) {
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const response = await fetchImpl(`${DiscordApi}${path}`, {
      ...init,
      signal: AbortSignal.timeout(15_000),
      headers: {
        authorization: `Bot ${token}`,
        ...(init.body ? { 'content-type': 'application/json' } : {}),
        ...init.headers
      }
    });
    const text = await response.text();
    let payload = null;
    try {
      payload = text ? JSON.parse(text) : null;
    } catch {
      payload = null;
    }
    if (response.ok) return payload;

    if (response.status === 429 && attempt < 3) {
      const retryAfterSeconds = Number(
        payload?.retry_after ?? response.headers.get('retry-after'));
      if (!Number.isFinite(retryAfterSeconds) ||
          retryAfterSeconds < 0 ||
          retryAfterSeconds > 30) {
        throw new Error(`Discord returned an invalid retry interval for ${path}.`);
      }
      await new Promise(resolve => setTimeout(resolve, retryAfterSeconds * 1000));
      continue;
    }
    if (response.status >= 500 && attempt < 3) {
      await new Promise(resolve => setTimeout(resolve, 250 * (2 ** attempt)));
      continue;
    }

    const detail = payload?.message ?? `HTTP ${response.status}`;
    throw new Error(`Discord ${init.method ?? 'GET'} ${path} failed: ${detail}.`);
  }
  throw new Error(`Discord ${init.method ?? 'GET'} ${path} exhausted its retry budget.`);
}

export function migrateLegacyWorkspaceComponents(message, applicationId, botId) {
  if (message?.author?.id !== botId ||
      message.application_id && message.application_id !== applicationId ||
      !Array.isArray(message.components)) {
    return null;
  }

  let changed = false;
  const components = message.components.map(row => ({
    ...row,
    components: Array.isArray(row.components)
      ? row.components.map(component => {
          if (component.type !== 2 ||
              component.label !== LegacyWorkspaceLabel ||
              !component.custom_id?.startsWith(WorkspaceActionPrefix)) {
            return component;
          }
          changed = true;
          return { ...component, label: WorkspaceLabel };
        })
      : row.components
  }));
  return changed ? components : null;
}

export async function reconcileLegacyWorkspaceLabels(
  fetchImpl,
  token,
  channelId,
  applicationId,
  botId) {
  let before = null;
  let updated = 0;
  for (let page = 0; page < MessagePageLimit; page += 1) {
    const query = before ? `?limit=100&before=${before}` : '?limit=100';
    const messages = await discordRequest(
      fetchImpl,
      token,
      `/channels/${channelId}/messages${query}`);
    if (!Array.isArray(messages)) {
      throw new Error('Discord did not return the configured channel message history.');
    }

    for (const message of messages) {
      const components = migrateLegacyWorkspaceComponents(
        message,
        applicationId,
        botId);
      if (!components) continue;

      await discordRequest(
        fetchImpl,
        token,
        `/channels/${channelId}/messages/${message.id}`,
        {
          method: 'PATCH',
          body: JSON.stringify({ components })
        });
      updated += 1;
    }

    if (messages.length < 100) return updated;
    before = messages.at(-1)?.id;
    if (!before) {
      throw new Error('Discord channel history could not advance to the next page.');
    }
  }
  throw new Error(
    `Discord channel history exceeded the ${MessagePageLimit * 100}-message reconciliation limit.`);
}

export async function reconcileDiscordGuild(configuration, fetchImpl = fetch) {
  const token = requireValue(configuration, 'DISCORD_BOT_TOKEN');
  if (!/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)) {
    throw new Error('DISCORD_BOT_TOKEN is not a Discord bot token.');
  }
  const applicationId = requireValue(configuration, 'DISCORD_APPLICATION_ID');
  const guildId = requireValue(configuration, 'DISCORD_GUILD_ID');
  const channelName = requireValue(configuration, 'DISCORD_CHANNEL_NAME');
  const parentId = requireValue(configuration, 'DISCORD_CHANNEL_PARENT_ID');
  const logChannelName =
    configuration.DISCORD_LOG_CHANNEL_NAME?.trim() || `${channelName}-log`;
  if (logChannelName === channelName) {
    throw new Error('DISCORD_LOG_CHANNEL_NAME must differ from DISCORD_CHANNEL_NAME.');
  }

  const bot = await discordRequest(fetchImpl, token, '/users/@me');
  if (bot.id !== applicationId || bot.bot !== true) {
    throw new Error('The Discord credential does not belong to the configured bot application.');
  }

  const guilds = await discordRequest(fetchImpl, token, '/users/@me/guilds');
  const guild = guilds.find(candidate => candidate.id === guildId);
  if (!guild) {
    throw new Error(`The bot has not joined configured guild ${guildId}.`);
  }

  const permissions = BigInt(guild.permissions);
  if ((permissions & Administrator) === 0n &&
      (permissions & RequiredPermissions) !== RequiredPermissions) {
    throw new Error('The bot lacks Manage Channels, View Channels, Send Messages, or Embed Links.');
  }

  const canSetOverwrites = (permissions & (Administrator | ManageRoles)) !== 0n;
  const channels = await discordRequest(fetchImpl, token, `/guilds/${guildId}/channels`);
  const reuseOrCreateTextChannel = async (name, topic, permissionOverwrites = null) => {
    const matches = channels.filter(channel =>
      channel.name === name &&
      channel.parent_id === parentId &&
      channel.type === 0);
    if (matches.length > 1) {
      throw new Error(`More than one #${name} text channel exists under the configured category.`);
    }

    const createBody = {
      name,
      type: 0,
      parent_id: parentId,
      topic
    };
    if (canSetOverwrites && Array.isArray(permissionOverwrites) && permissionOverwrites.length > 0) {
      createBody.permission_overwrites = permissionOverwrites;
    }

    return matches[0] ?? await discordRequest(
      fetchImpl,
      token,
      `/guilds/${guildId}/channels`,
      {
        method: 'POST',
        body: JSON.stringify(createBody)
      });
  };

  const channel = await reuseOrCreateTextChannel(
    channelName,
    'Craft Architect commission briefs: scope, material responsibility, payment basis, and evidence in one place.');
  const logChannel = await reuseOrCreateTextChannel(
    logChannelName,
    'Claimed and completed Craft Architect commissions.',
    channel.permission_overwrites);
  if (logChannel.id === channel.id) {
    throw new Error('The commission log channel resolved to the open commission channel.');
  }

  const commands = await discordRequest(
    fetchImpl,
    token,
    `/applications/${applicationId}/guilds/${guildId}/commands`,
    {
      method: 'PUT',
      body: JSON.stringify([
        {
          name: 'commission',
          description: 'Publish a Craft Architect commission brief',
          type: 1,
          dm_permission: false,
          options: [
            {
              type: 1,
              name: 'post',
              description: 'Post a published commission brief',
              options: [
                {
                  type: 3,
                  name: 'brief',
                  description: 'Craft Architect commission brief link',
                  required: true
                }
              ]
            }
          ]
        }
      ])
    });
  if (commands.length !== 1 || commands[0].name !== 'commission') {
    throw new Error('Discord did not retain the managed commission command.');
  }

  let legacyWorkspaceLabelsUpdated = 0;
  for (const channelId of [channel.id, logChannel.id]) {
    legacyWorkspaceLabelsUpdated += await reconcileLegacyWorkspaceLabels(
      fetchImpl,
      token,
      channelId,
      applicationId,
      bot.id);
  }

  return {
    applicationId,
    botId: bot.id,
    guildId,
    guildName: guild.name,
    channelId: channel.id,
    channelName: channel.name,
    logChannelId: logChannel.id,
    logChannelName: logChannel.name,
    commandId: commands[0].id,
    legacyWorkspaceLabelsUpdated
  };
}

async function main() {
  const result = await reconcileDiscordGuild(process.env);
  if (process.env.GITHUB_OUTPUT) {
    await appendFile(
      process.env.GITHUB_OUTPUT,
      `channel_id=${result.channelId}\nlog_channel_id=${result.logChannelId}\n` +
        `command_id=${result.commandId}\n`,
      'utf8');
  }
  console.log(
    `Discord integration ready: ${result.guildName} / #${result.channelName} + ` +
    `#${result.logChannelName} / commission; ` +
    `${result.legacyWorkspaceLabelsUpdated} legacy workspace label(s) updated.`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
