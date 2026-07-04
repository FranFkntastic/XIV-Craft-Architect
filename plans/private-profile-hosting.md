# Private Hosted Profiles

Date: 2026-07-04
Status: Implemented foundation; disabled by default on hosted helper deployments

## Scope

Private hosted profiles let a user sync one Craft Architect browser profile against a self-hosted server they control. This is not a public account system. Access is gated by per-profile access keys created on the server.

Synced collections:

- app settings, excluding profile-host connection metadata
- saved recipe plans
- active Trade company profile
- Trade crafter roster
- Trade orders
- Trade payroll drafts

The client remains local-first. IndexedDB is still the browser working store; the hosted profile service is a sync target.

## Server Configuration

The profile host runs inside `FFXIV Craft Architect.LodestoneLookup` and is disabled unless explicitly enabled.

Configuration keys:

```text
ProfileHost__Enabled=true
ProfileHost__DatabasePath=/srv/craftarchitect/services/lodestone/data/profile-host.db
```

The VPS deployment workflow creates:

```text
/srv/craftarchitect/services/lodestone/data
/srv/craftarchitect/services/lodestone/env/profile-host.env
```

The default env file keeps the feature off:

```text
ProfileHost__Enabled=false
ProfileHost__DatabasePath=/srv/craftarchitect/services/lodestone/data/profile-host.db
```

To enable on the VPS, edit the env file, set `ProfileHost__Enabled=true`, and restart the helper:

```bash
sudo systemctl restart craftarchitect-lodestone
curl https://dev.xivcraftarchitect.com/api/profile-host/health
```

Expected enabled health response includes:

```json
{
  "profileHostEnabled": true
}
```

## Caddy Route

The browser profile Host URL should point at the API prefix, for example:

```text
https://dev.xivcraftarchitect.com/api/
```

That means Caddy must proxy `/api/profile-host/*` to the helper, stripping `/api` the same way the Lodestone routes do:

```caddyfile
handle /api/profile-host/* {
    uri strip_prefix /api
    reverse_proxy 127.0.0.1:5128
}
```

Keep the existing `/api/lodestone/*` and `/api/xivdata/*` routes.

## Profile Provisioning

Run provisioning commands from the current helper release directory so they use the same env file and database path as the service:

```bash
cd /srv/craftarchitect/services/lodestone/current
set -a
source /srv/craftarchitect/services/lodestone/env/profile-host.env
set +a
./"FFXIV Craft Architect.LodestoneLookup" profile-host create-profile "Sapphire Avenue Trade Company"
```

The command prints JSON containing `profileId`, `displayName`, and a plaintext `accessKey`. Copy the access key once; only a hash is stored in SQLite.

Rotate a profile key:

```bash
./"FFXIV Craft Architect.LodestoneLookup" profile-host rotate-key <profile-id>
```

Disable a profile and revoke its keys:

```bash
./"FFXIV Craft Architect.LodestoneLookup" profile-host disable-profile <profile-id>
```

Export server-side profile objects for inspection or backup:

```bash
./"FFXIV Craft Architect.LodestoneLookup" profile-host export-profile <profile-id> > profile-export.json
```

## Client Setup

In the app, open `Tools > Options > Hosting`.

Fields:

- Host URL: `https://dev.xivcraftarchitect.com/api/` or another private host API prefix
- Access key: the plaintext key printed during provisioning
- First connect:
  - `Upload this browser first` seeds an empty/new hosted profile from current browser state.
  - `Download server profile first` applies server state into the current browser.

The status card shows connection state, host reachability, last revision, queued writes, and conflicts. Conflicts can be resolved by applying the remote version or keeping the local version.

## Operations Notes

- The SQLite database lives outside release directories and survives helper deploys.
- Release rollback is still a `current` symlink operation; the database does not roll back with app binaries.
- Back up `/srv/craftarchitect/services/lodestone/data/profile-host.db` before schema-changing releases.
- Profile hosting is intentionally not exposed as a signup or admin UI.
- Do not put access keys in repository secrets unless a workflow needs to provision or smoke-test a private profile.
