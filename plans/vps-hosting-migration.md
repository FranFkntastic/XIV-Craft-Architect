# FFXIV Craft Architect VPS Hosting Migration

Date: 2026-06-20
Branch: vps-hosting-migration
Status: VPS migration active; GitHub Pages retired as an app host

## Goal

Move the public web build of **FFXIV Craft Architect** away from GitHub Pages to hosting that can also run backend services.

This is not only about getting a custom domain. The important unblocker is the Lodestone lookup flow:

- Blazor WebAssembly can serve the frontend as static files.
- Lodestone import currently requires a non-browser host because direct browser retrieval is blocked by browser/CORS constraints.
- `FFXIV Craft Architect.LodestoneLookup` already exists as a small ASP.NET Core helper.
- A VPS can host both the static Blazor app and the Lodestone helper under one domain.

## Current Deployment Shape

Current deployment shape as of 2026-06-22:

- `https://xivcraftarchitect.com` is the canonical `main` host on the VPS.
- `https://dev.xivcraftarchitect.com` is the canonical `local-dev` host on the VPS.
- `.github/workflows/deploy-vps-web.yml` is the automatic web deployment path for VPS-hosted web builds.
- `.github/workflows/deploy-vps-lodestone.yml` is the manual deployment path for the hosted Lodestone helper.
- `.github/workflows/deploy-web.yml` is a manual GitHub Pages moved-notice deploy for old Pages visitors.

Legacy GitHub Pages app workflow shape:

- Builds `src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj`.
- Publishes `main` and `local-dev`.
- Copies `main` `wwwroot` to the Pages root.
- Copies `local-dev` `wwwroot` to `/local-dev/`.
- Uses GitHub Pages deployment actions.

Current GitHub Pages notice workflow:

- Does not build or publish the Blazor app.
- Publishes a static moved notice at the old Pages root.
- Publishes a cryptic local-dev notice at the old `/local-dev/` route without advertising the dev URL.
- Uses `404.html` to show the same notice for old deep links.

Current web app shape:

- `FFXIV Craft Architect.Web` is a Blazor WebAssembly project.
- The release publish output is static files under `wwwroot`.
- `wwwroot/index.html` contains GitHub Pages base-path logic for `/XIV-Craft-Architect/` and `/XIV-Craft-Architect/local-dev/`.
- `wwwroot/404.html` exists to make GitHub Pages tolerate client-side routes.
- Browser persistence uses IndexedDB, which is origin-scoped.

Current Lodestone shape:

- `FFXIV Craft Architect.Web/wwwroot/appsettings.json` points `LodestoneLookup:BaseAddress` at `http://localhost:5128/`.
- `FFXIV Craft Architect.LodestoneLookup` exposes:
  - `GET /`
  - `GET /lodestone/crafters/search?name=...&world=...&dataCenter=...`
  - `GET /lodestone/crafters/{characterId}/preview`
- The helper runs NetStone outside the browser.
- The future VPS-hosted version should preserve this endpoint shape and change only the host/base address if possible.

## Target Architecture

Preferred first hosted architecture:

```text
https://craftarchitect.example.com
  -> Caddy
      -> static Blazor WebAssembly files

https://craftarchitect.example.com/api/lodestone/...
  -> Caddy
      -> reverse proxy to ASP.NET Core helper on localhost
```

Suggested internal service ports:

```text
Caddy public HTTP/HTTPS: 80/443
Lodestone helper:        127.0.0.1:5128
```

The browser should call the same public origin where possible. That avoids most CORS and Private Network Access complexity:

```text
Browser -> https://craftarchitect.example.com/api/lodestone/...
Caddy   -> http://127.0.0.1:5128/lodestone/...
Helper  -> Lodestone
```

## Why Caddy

Caddy is the recommended web server for the first VPS version.

Reasons:

- It can serve static files.
- It can reverse-proxy API routes to the .NET helper.
- It manages HTTPS certificates automatically when DNS points at the server.
- Its configuration is smaller than the equivalent Nginx setup.
- It is beginner-friendly enough while still being production-capable.

Reference docs:

- Caddy install: https://caddyserver.com/docs/install
- Caddy automatic HTTPS: https://caddyserver.com/docs/automatic-https
- Caddy file server: https://caddyserver.com/docs/caddyfile/directives/file_server
- Caddy reverse proxy: https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- Caddy `try_files`: https://caddyserver.com/docs/caddyfile/directives/try_files

Draft Caddyfile shape:

```caddyfile
craftarchitect.example.com {
    handle /api/lodestone/* {
        uri strip_prefix /api
        reverse_proxy 127.0.0.1:5128
    }

    root * /srv/craftarchitect/web/current
    try_files {path} {path}/ /index.html
    file_server {
        precompressed br gzip
    }
}
```

This is a starting point, not yet tested against the app.

## Web Server / Reverse Proxy Options

The required hosting role is:

```text
Browser
  -> web server / reverse proxy
      -> static files for Blazor WebAssembly
      -> ASP.NET Core Lodestone helper API
```

Caddy is the current recommendation, but it is not the only viable choice.

### Caddy

Best first VPS default.

Pros:

- Automatic HTTPS is built in.
- Small configuration surface.
- Serves static files well.
- Reverse-proxies API routes cleanly.
- Good match for one domain, one Blazor frontend, and one helper service.

Cons:

- Smaller ecosystem than Nginx.
- Fewer old StackOverflow answers and provider-specific examples.

### Nginx

Most standard professional/sysadmin option.

Pros:

- Very widely used.
- Excellent static file server and reverse proxy.
- Many ASP.NET Core Linux deployment guides use Nginx.
- Easy to hire/search/debug against because it is everywhere.

Cons:

- HTTPS usually means learning Certbot/ACME plumbing or provider-specific setup.
- Configuration is more verbose and easier to subtly misconfigure.

### Apache

Mature and capable, but not the preferred fresh start.

Pros:

- Old, stable, common.
- Can serve static files and proxy to ASP.NET Core.

Cons:

- Less pleasant for a modern SPA plus API reverse-proxy shape.
- More legacy-web-server complexity than this project needs.

### Traefik

Good if the project later becomes Docker-heavy.

Pros:

- Automatic HTTPS.
- Strong Docker Compose integration.
- Useful for many small services.

Cons:

- More abstract than Caddy or Nginx.
- Overkill for the first one-frontend/one-helper VPS.

### Nginx Proxy Manager

GUI wrapper around Nginx and Let's Encrypt.

Pros:

- Friendly web UI.
- Popular for homelab/self-hosted setups.
- Useful when managing many domains/subdomains.

Cons:

- Usually means introducing Docker.
- Adds an admin panel that must be secured.
- Indirect: manage the manager, which manages Nginx.

### HAProxy

Excellent proxy/load balancer, but the wrong default here.

Pros:

- Extremely strong reverse proxy/load balancer.

Cons:

- Not static-site-first.
- Unnecessary unless the app grows into multiple backend servers.

Current ranking:

```text
1. Caddy
2. Nginx
3. Nginx Proxy Manager, if Docker enters the plan
4. Traefik, if the app becomes a multi-container stack
5. Apache
6. HAProxy
```

Choosing Caddy does not lock the app into Caddy. The durable architecture remains static Blazor files plus ASP.NET Core helper on localhost behind a reverse proxy. Swapping Caddy for Nginx later should mostly be a server configuration change.

## Linux Distribution

Original recommended VPS distro:

```text
Ubuntu Server 24.04 LTS
```

Purchased VPS distro:

```text
Ubuntu 26.04
```

Reasons:

- Most beginner/server guides target Ubuntu.
- .NET support is strong.
- Caddy install docs cover Ubuntu/Debian cleanly.
- systemd is standard.
- Security updates are straightforward.
- Most VPS providers offer it.
- Troubleshooting is easier because the ecosystem is large.

Good alternatives:

```text
Debian 12
Ubuntu Server 22.04 LTS
```

Avoid for the first VPS pass:

```text
Alpine
Arch
Fedora Server
CentOS / Rocky / Alma
provider-custom control-panel images
```

Debian 12 is also a good server choice, but Ubuntu 24.04 LTS was the better beginner default for the initial plan.

Ubuntu 26.04 is still viable. The main adjustment is .NET:

- Ubuntu 26.04 ships with .NET 10.
- **FFXIV Craft Architect** currently targets `net8.0`.
- Microsoft notes that .NET 8 on Ubuntu 26.04 is available through the `dotnet/backports` PPA with best-effort support.
- To avoid installing a .NET 8 runtime on the server for the first pass, prefer publishing `FFXIV Craft Architect.LodestoneLookup` as a self-contained `linux-x64` build.

Relevant source:

- .NET on Ubuntu 26.04: https://devblogs.microsoft.com/dotnet/whats-new-for-dotnet-in-ubuntu-2604/

## Docker Position

Docker is useful but not required for the first migration.

Working definition:

```text
VPS = rented computer
Docker = app boxes running on that computer
Container = one running app box
Image = recipe/snapshot used to create the box
Dockerfile = instructions for building the image
Docker Compose = one file that starts multiple boxes together
```

Docker could eventually run:

```text
container 1: Caddy
container 2: LodestoneLookup .NET API
volume/folder: published Blazor static files
```

Reasons to defer Docker:

- It is another layer to learn.
- Logs, networking, and volumes add new concepts.
- The first target only needs static files plus one small .NET helper.
- Installing Caddy normally and running the helper as a systemd service teaches the core server pieces directly.

Reasons Docker may earn its keep later:

- Multiple backend services.
- Postgres/Redis or other infrastructure.
- Fully isolated branch preview stacks.
- More complex rollback/deploy automation.
- Reproducible local/staging/prod environments.

Current decision:

```text
Do not use Docker for the first VPS migration.
Revisit Docker after static hosting and hosted Lodestone helper are working.
```

## Multiple Branch Versions Without Docker

The current GitHub Pages workflow publishes both `main` and `local-dev`. A VPS can preserve that idea without Docker by using separate folders and Caddy routes/subdomains.

Path-based shape:

```text
craftarchitect.example.com
  -> main

craftarchitect.example.com/local-dev
  -> local-dev
```

Subdomain-based shape:

```text
craftarchitect.example.com
  -> main

dev.craftarchitect.example.com
  -> local-dev
```

Prefer subdomains over paths for this app.

Reasons:

- Blazor base-path handling is simpler.
- IndexedDB storage is separated by origin.
- Dev schemas cannot poison prod browser storage.
- Caddy config is clearer.
- API routing can stay clean per environment.

Suggested folder layout:

```text
/srv/craftarchitect/web/main/current
/srv/craftarchitect/web/local-dev/current
```

Suggested helper ports if each branch needs its own backend:

```text
main helper:      127.0.0.1:5128
local-dev helper: 127.0.0.1:5129
```

Draft Caddy shape:

```caddyfile
craftarchitect.example.com {
    handle /api/lodestone/* {
        uri strip_prefix /api
        reverse_proxy 127.0.0.1:5128
    }

    root * /srv/craftarchitect/web/main/current
    try_files {path} {path}/ /index.html
    file_server {
        precompressed br gzip
    }
}

dev.craftarchitect.example.com {
    handle /api/lodestone/* {
        uri strip_prefix /api
        reverse_proxy 127.0.0.1:5129
    }

    root * /srv/craftarchitect/web/local-dev/current
    try_files {path} {path}/ /index.html
    file_server {
        precompressed br gzip
    }
}
```

This gives most of the branch-version benefit without introducing container infrastructure.

## VPS Options

Prices below were checked on 2026-06-20 and should be rechecked before purchase.

### Recommended Shortlist

#### OVHcloud VPS-1

Source: https://us.ovhcloud.com/vps/

Observed listing:

- `$4.54/month`
- `2 vCores`
- `4 GB RAM`
- `40 GB SSD`
- Daily backup of previous 24 hours
- Unlimited traffic
- `200 Mbps` public bandwidth

Why it is currently the default recommendation:

- Fits the target budget.
- Enough RAM to run Caddy plus a small ASP.NET Core service comfortably.
- Daily backup is useful for a beginner VPS.
- More runway than a 1 GB instance without jumping into a much higher tier.

Risks / cautions:

- OVH panel and product naming can be less polished than DigitalOcean.
- Recheck final tax/region/monthly price before buying.

#### Hetzner CX22 / Cost-Optimized Shared vCPU

Sources:

- https://www.hetzner.com/cloud/cost-optimized
- https://www.hetzner.com/pressroom/new-cx-plans/

Observed listing:

- Hetzner cost-optimized page shows an entry point around `EUR 5.99 max/month`.
- Historical CX22 reference listed `2 vCPUs`, `4 GB RAM`, and `40 GB disk`.

Why consider it:

- Often excellent price/performance.
- Good developer reputation.
- US locations exist, including Ashburn, Virginia and Hillsboro, Oregon.

Risks / cautions:

- Pricing has been changing in 2026, so verify at checkout.
- Backups may be extra depending on selected options.
- Account approval/friction can occasionally happen.

#### IONOS VPS XS / S

Source: https://www.ionos.com/servers/vps-usa

Observed listing:

- VPS XS: `$2/month` with a 3-year term, `1 vCore`, `1 GB RAM`, `10 GB NVMe`.
- VPS S: promotional `$3/month` for 12 months with a 3-year term, `2 vCores`, `2 GB RAM`, `80 GB NVMe`.

Why consider it:

- Cheapest credible VPS entry point.
- Fine for static files plus a very small helper if traffic is low.

Risks / cautions:

- Long-term commitment is the catch.
- 1 GB RAM is cramped for learning and for future expansion.
- Less comfortable default than OVHcloud VPS-1.

### Other Viable Providers

#### DigitalOcean Basic Droplet

Source: https://www.digitalocean.com/pricing/droplets

Observed listing:

- Starts around `$4/month`.
- Entry plan includes low resources.
- Outbound transfer starts around `500 GiB/month`.

Why consider it:

- Very beginner-friendly docs and UI.
- Predictable developer experience.

Why not the current default:

- Comparable 4 GB RAM plans are much more expensive than OVHcloud/Hetzner.
- The cheapest plan may be too cramped once the helper and future services matter.

#### Akamai / Linode Shared CPU

Source: https://www.akamai.com/cloud/pricing

Observed listing:

- Nanode 1 GB around `$5/month`.
- Larger shared CPU plans jump upward from there.

Why consider it:

- Stable, familiar VPS provider.
- Good docs and predictable billing.

Why not the current default:

- Less RAM per dollar than OVHcloud or Hetzner at the budget tier.

#### Vultr

Source: https://www.vultr.com/pricing/

Why consider it:

- Developer-friendly.
- Good global footprint.
- Easy to spin up and destroy instances.

Why not the current default:

- Usually more expensive for the same RAM/CPU compared with OVHcloud/Hetzner.
- Better as a convenience pick than a budget pick.

#### Contabo

Source: https://contabo.com/en-us/pricing/

Observed listing:

- Entry cloud VPS plans around `EUR 5.50/month`.
- Often high RAM/storage for the price.

Why consider it:

- Very resource-heavy plans for cheap.

Why not the current default:

- Oversold-resource reputation compared with more conservative providers.
- More of a "cheap big box" pick than a smooth first production host.

#### Hostinger VPS

Source: https://www.hostinger.com/vps-hosting

Observed listing:

- KVM VPS plan range shown around `$6.49-$25.99/month`.

Why consider it:

- Beginner-oriented product surface.

Why not the current default:

- Promotional/term pricing can be confusing.
- Less attractive than OVHcloud/Hetzner for this specific .NET + Caddy target.

## Domain / DNS

Cloudflare remains a good domain and DNS choice.

Recommended shape:

```text
Registrar/DNS: Cloudflare
VPS:           OVHcloud VPS-1, Hetzner CX22-class, or similar
Web server:    Caddy
```

DNS records:

```text
A     craftarchitect       <VPS IPv4>
AAAA  craftarchitect       <VPS IPv6>    optional, only if configured
```

Actual purchased values:

```text
Domain:        xivcraftarchitect.com
Provider:      OVHcloud
Public IPv4:   51.222.141.175
Public IPv6:   2607:5300:229:4e4::1
Distro:        Ubuntu 26.04
Initial user:  ubuntu
SSH method:    password
```

Verified host facts from first SSH login:

```text
Hostname:      vps-47b95ef3
Virtualization: kvm
OS:            Ubuntu 26.04 LTS
Codename:      resolute
Kernel:        Linux 7.0.0-14-generic
Architecture:  x86-64
Interface:     ens3
IPv4:          51.222.141.175/32
IPv6:          2607:5300:229:4e4::1/128
Initial user:  ubuntu
```

Bootstrap progress:

- Initial SSH login succeeded as `ubuntu`.
- Ubuntu package update/upgrade completed and the VPS rebooted successfully.
- Post-reboot SSH login succeeded.
- `xivcraftarchitect.com` resolves to `51.222.141.175`.
- `dev.xivcraftarchitect.com` resolves to `51.222.141.175`.
- Caddy is installed and serving both public hosts over HTTPS.
- Static web deploy proof succeeded:
  - `https://xivcraftarchitect.com` serves the `main` web build.
  - `https://dev.xivcraftarchitect.com` serves the `local-dev` web build.
- SSH key login is configured locally with the `craftarchitect-vps` alias.
- `FFXIV Craft Architect.LodestoneLookup` is installed as a self-contained systemd service:
  - service: `craftarchitect-lodestone`
  - port: `127.0.0.1:5128`
  - wrapper: `/srv/craftarchitect/services/lodestone/run-lodestone.sh`
  - current release: `/srv/craftarchitect/services/lodestone/current`
- Caddy proxies hosted Lodestone lookup routes:
  - `https://xivcraftarchitect.com/api/lodestone/*`
  - `https://dev.xivcraftarchitect.com/api/lodestone/*`
- Hosted helper proof succeeded with known Lodestone data:
  - `Level Checker` search on Behemoth returned character id `16331040`.
  - Preview for `16331040` returned eight DoH job levels at `100`.
- Hosted region-scope proof succeeded after redeploying helper release `20260622181945`:
  - The browser/client request model now carries `Region`.
  - The helper endpoint accepts `region`.
  - Region-only searches fan out to data-center-scoped Lodestone searches instead of falling back to an unscoped/global search.
  - `region=North America` for `Wei Ning` returned NA data-center candidates such as `Aether`, `Primal`, and `Crystal`; a separate `dataCenter=Light` search returned the European candidate.
- `dev.xivcraftarchitect.com` has been manually redeployed with `LodestoneLookup:BaseAddress` set to `/api/lodestone/`.
- `dev.xivcraftarchitect.com` was corrected to an absolute `LodestoneLookup:BaseAddress` of `https://dev.xivcraftarchitect.com/api/` because `HttpLodestoneCrafterLookupService` appends `lodestone/...` itself and the browser resolved relative `/api/` config against `file:///`.
- Caddy now sends `Cache-Control: no-cache` for `/`, `/index.html`, and `/appsettings.json` so runtime config changes are revalidated by browsers.

Current branch caveat:

- The current `main` source does not yet include the Lodestone web lookup integration present on `local-dev`.
- Hosted Lodestone import is therefore immediately testable on `dev.xivcraftarchitect.com`.
- `xivcraftarchitect.com` can keep serving `main` unchanged until the feature/config changes are promoted to `main`.

If using Cloudflare proxy mode, start with DNS-only while bringing up Caddy and HTTPS. After the direct setup works, evaluate whether proxy mode helps or complicates anything.

## Development Options Unlocked By VPS

A VPS expands the project beyond GitHub Pages/static hosting:

- Host the existing ASP.NET Core Lodestone helper.
- Put frontend and helper behind one public domain.
- Add server-side caching for Lodestone, Universalis, Garland, or Teamcraft calls.
- Add rate limiting and request logging.
- Add scheduled refresh jobs.
- Add a small database later, likely SQLite first.
- Add account/cloud sync later if local-first storage becomes limiting.
- Host staging/dev variants under subdomains.
- Run background diagnostics or admin endpoints.

## Migration Phases

### Phase 0: Buy And Prepare Hosting

- Choose domain/DNS provider.
- Choose VPS.
- Create server.
- Lock down SSH.
- Install Caddy.
- Install .NET runtime or deploy self-contained .NET helper.

### Phase 0 Runbook: After Purchase

Information to collect before starting:

```text
Domain name: xivcraftarchitect.com
VPS provider: OVHcloud
VPS public IPv4: 51.222.141.175
VPS public IPv6, if any: 2607:5300:229:4e4::1
Linux image: Ubuntu 26.04
Initial SSH username: ubuntu
SSH access method: password
```

Do not paste server passwords into chat. If password login is the only initial access path, type it locally when `ssh` prompts.

OVHcloud note:

- If the order status is still `being processed`, wait for provisioning to finish before looking for the IP or SSH details.
- Once active, OVH should show the VPS in the OVHcloud Control Panel under the VPS/server product area.
- The IPv4 address, hostname, installed OS, and login details are usually visible in the Control Panel and/or sent in the installation email.
- The default SSH username depends on the OS image. OVH documentation gives examples such as `ubuntu` for Ubuntu and `debian` for Debian.
- OVH documentation says each VPS is delivered with IPv4 and IPv6, but only IPv4 is configured by default. IPv6 can be configured later; it is not required for the first deployment.

Useful OVH source docs:

- Getting started with a VPS: https://docs.ovhcloud.com/en/guides/bare-metal-cloud/virtual-private-servers/starting-with-a-vps
- SSH introduction: https://docs.ovhcloud.com/en/guides/bare-metal-cloud/dedicated-servers/ssh-introduction
- Configure IPv6 on a VPS: https://docs.ovhcloud.com/en/guides/bare-metal-cloud/virtual-private-servers/configure-ipv6

First DNS records to create in Cloudflare:

```text
Type  Name   Value       Proxy
A     @      51.222.141.175  DNS only, at first
A     dev    51.222.141.175  DNS only, at first
```

Optional, if the VPS has IPv6 configured:

```text
AAAA  @      2607:5300:229:4e4::1  DNS only, at first
AAAA  dev    2607:5300:229:4e4::1  DNS only, at first
```

Start DNS-only so Caddy can request HTTPS certificates directly. Cloudflare proxy mode can be evaluated after direct HTTPS works.

Initial local SSH check from PowerShell:

```powershell
ssh ubuntu@51.222.141.175
```

Type the OVH-provided password locally when prompted.

On the VPS, confirm the server:

```bash
whoami
hostnamectl
lsb_release -a
ip addr
```

First Ubuntu update:

```bash
apt update
apt upgrade -y
reboot
```

After reboot, reconnect:

```powershell
ssh ubuntu@51.222.141.175
```

Create a deploy/admin user if the initial `ubuntu` user has sudo and we want a separate deployment identity:

```bash
adduser deploy
usermod -aG sudo deploy
```

Then reconnect as that user:

```powershell
ssh deploy@51.222.141.175
```

Install baseline packages:

```bash
sudo apt update
sudo apt install -y curl unzip ufw
```

For Ubuntu 26.04, prefer self-contained deployment for the Lodestone helper instead of installing .NET 8 on the server.

If a framework-dependent deploy is later needed, install .NET 8 via Ubuntu's backports PPA:

```bash
sudo apt install -y software-properties-common
sudo add-apt-repository ppa:dotnet/backports
sudo apt update
sudo apt install -y aspnetcore-runtime-8.0
dotnet --info
```

Do not run this until we decide framework-dependent deployment is better than self-contained deployment.

Install Caddy:

```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg
sudo chmod o+r /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install -y caddy
caddy version
systemctl status caddy --no-pager
```

Open only the expected firewall ports:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
sudo ufw status verbose
```

Create app directories:

```bash
sudo mkdir -p /srv/craftarchitect/web/main/releases
sudo mkdir -p /srv/craftarchitect/web/local-dev/releases
sudo mkdir -p /srv/craftarchitect/services/lodestone
sudo chown -R deploy:deploy /srv/craftarchitect
```

Minimal placeholder Caddyfile for first HTTPS proof:

```caddyfile
craftarchitect.example.com {
    respond "FFXIV Craft Architect host is alive"
}

dev.craftarchitect.example.com {
    respond "FFXIV Craft Architect dev host is alive"
}
```

Validate and reload:

```bash
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

First acceptance check:

```text
https://craftarchitect.example.com
https://dev.craftarchitect.example.com
```

Both should load over HTTPS before app deployment starts.

### Phase 1: Static Web On VPS

- Publish `FFXIV Craft Architect.Web`.
- Copy `wwwroot` to the VPS.
- Serve it with Caddy.
- Configure route fallback to `index.html`.
- Verify direct route refreshes.
- Keep GitHub Pages alive as fallback.

### Phase 2: Hosted Lodestone Helper

- Publish `FFXIV Craft Architect.LodestoneLookup`.
- Run it as a systemd service on `127.0.0.1:5128`.
- Reverse-proxy `/api/lodestone/*` to the helper.
- Change web config from `http://localhost:5128/` to the hosted same-origin route.
- Verify search and preview with known character data.

### Phase 3: Deployment Automation

- Replace GitHub Pages deploy action with a VPS deploy workflow.
- Start with manual `dotnet publish` + `scp` or `rsync`.
- Later use GitHub Actions over SSH.
- Keep release directories and a `current` symlink for rollback.

### Phase 4: Hardening

- Enable firewall rules for SSH, HTTP, and HTTPS only.
- Add service restart policies.
- Add log review commands.
- Add backups for config and any future server-side data.
- Add uptime monitoring.
- Add explicit API rate limiting if needed.

## Open Decisions

- Final VPS provider.
- Domain name and whether to use apex or subdomain.
- Whether `local-dev` should remain a path, become a subdomain, or stop deploying publicly.
- Whether the helper should be routed as `/api/lodestone/...` or `api.craftarchitect.example.com`.
- Whether to publish the helper framework-dependent or self-contained.
- Whether initial deploys should be manual or GitHub Actions immediately.
- Whether server-side storage should stay out of scope for this migration.

## Implementation Action Plan

Status: Proposed; wait for approval before making code/workflow changes.

### Guiding Principles

- Keep GitHub Pages available only as a moved-notice surface once the VPS-hosted app and helper are verified.
- Prove the hosted Lodestone helper manually before building GitHub Actions automation around it.
- Prefer same-origin API paths on the VPS to avoid unnecessary CORS complexity.
- Preserve `main` and `local-dev` as separate public environments:
  - `https://xivcraftarchitect.com` for `main`
  - `https://dev.xivcraftarchitect.com` for `local-dev`
- Keep Docker out of the first migration.
- Prefer self-contained deployment for `FFXIV Craft Architect.LodestoneLookup` on Ubuntu 26.04 because the app targets `net8.0` and the server ships with newer .NET packages.

### Phase 1: Manual Hosted Lodestone Helper Proof

Goal:

Run the existing Lodestone helper on the VPS and expose it through Caddy.

Work:

1. Publish `src/FFXIV Craft Architect.LodestoneLookup/FFXIV Craft Architect.LodestoneLookup.csproj` as a self-contained Linux build:

   ```powershell
   dotnet publish "src\FFXIV Craft Architect.LodestoneLookup\FFXIV Craft Architect.LodestoneLookup.csproj" -c Release -r linux-x64 --self-contained true -o ".tmp\vps-deploy\publish\lodestone"
   ```

2. Copy the helper publish output to the VPS.
3. Install it under `/srv/craftarchitect/services/lodestone/current`.
4. Add a `systemd` service, likely `craftarchitect-lodestone.service`, bound to `127.0.0.1:5128`.
5. Add Caddy reverse proxy routes:

   ```text
   https://xivcraftarchitect.com/api/lodestone/* -> http://127.0.0.1:5128/lodestone/*
   https://dev.xivcraftarchitect.com/api/lodestone/* -> http://127.0.0.1:5128/lodestone/*
   ```

   The first pass can share one helper between prod/dev because it is stateless lookup only. Split helper instances later if branch-specific helper behavior matters.

Verification:

```bash
systemctl status craftarchitect-lodestone --no-pager
curl http://127.0.0.1:5128/
curl https://xivcraftarchitect.com/api/lodestone/crafters/search?name=Level%20Checker\&world=Behemoth
curl https://dev.xivcraftarchitect.com/api/lodestone/crafters/16331040/preview
```

Go/no-go:

- Continue only if the helper returns the ready payload and known Lodestone search/preview results through Caddy.

### Phase 2: Web App Hosted-API Configuration

Goal:

Make the hosted web app use the VPS-hosted helper instead of user-local `http://localhost:5128/`.

Work:

1. Change `src/FFXIV Craft Architect.Web/wwwroot/appsettings.json` from:

   ```json
   {
     "LodestoneLookup": {
       "BaseAddress": "http://localhost:5128/"
     }
   }
   ```

   to an environment-specific hosted API route:

   ```json
   {
     "LodestoneLookup": {
       "BaseAddress": "https://dev.xivcraftarchitect.com/api/"
     }
   }
   ```

   `HttpLodestoneCrafterLookupService` appends `lodestone/...` to the configured base address, so `https://dev.xivcraftarchitect.com/api/` becomes `https://dev.xivcraftarchitect.com/api/lodestone/...`.
   A relative `/api/` path looked cleaner, but the deployed browser resolved it against `file:///` in practice, so absolute environment-specific API URLs are the safer first deployment path.
2. Keep `Program.cs` able to resolve absolute hosted API URLs and local-helper URLs. A future cleanup can add a stronger same-origin resolver if we want to avoid environment-specific `appsettings.json` values.
3. Publish and manually redeploy both web builds.
4. Test Lodestone import from both:
   - `https://xivcraftarchitect.com`
   - `https://dev.xivcraftarchitect.com`

Verification:

- Web app loads on both domains.
- Existing direct routes refresh correctly.
- Lodestone search and preview work without a local helper running on the user's machine.
- Browser console does not show CORS or failed `/api/lodestone` requests.

Go/no-go:

- Continue to automation only after hosted Lodestone import works in the browser.

### Phase 3: Remove Or Neutralize GitHub Pages Runtime Assumptions

Goal:

Stop carrying GitHub Pages behavior into the VPS-hosted app where it is no longer needed.

Candidate files:

- `src/FFXIV Craft Architect.Web/wwwroot/index.html`
- `src/FFXIV Craft Architect.Web/wwwroot/404.html`

Work:

1. Simplify base href logic if the app is no longer hosted under `/XIV-Craft-Architect/`.
2. Decide whether any GitHub Pages compatibility must remain temporarily in the app bundle.
3. If Pages-specific runtime logic remains, keep it from interfering with `xivcraftarchitect.com`.
4. Once the moved-notice workflow is trusted, remove Pages-specific base-path and sessionStorage redirect logic from the app bundle when safe.
5. Keep route fallback behavior in Caddy via:

   ```caddyfile
   try_files {path} {path}/ /index.html
   ```

Verification:

- Refresh direct app routes on both domains.
- Old `/market` compatibility redirect still behaves as intended if it remains required.
- The GitHub Pages moved notice still loads for old root, local-dev, and deep-link URLs.

Go/no-go:

- Do not delete the GitHub Pages workflow while old Pages URLs still need a friendly moved notice.

### Phase 4: VPS Deployment Automation

Goal:

Replace manual zip/scp deployment with GitHub Actions deployment to the VPS.

Proposed workflow shape:

```text
push to main:
  publish web main
  upload to /srv/craftarchitect/web/main/releases/<run>
  update /srv/craftarchitect/web/main/current
  reload Caddy if needed

push to local-dev:
  publish web local-dev
  upload to /srv/craftarchitect/web/local-dev/releases/<run>
  update /srv/craftarchitect/web/local-dev/current
  reload Caddy if needed

manual dispatch:
  optionally deploy both branches
  optionally deploy Lodestone helper
```

GitHub secrets needed:

```text
VPS_HOST=51.222.141.175
VPS_USER=ubuntu
VPS_SSH_PRIVATE_KEY=<deploy private key>
```

Implementation options:

- Use `scp`/`ssh` directly from Actions.
- Or use `rsync` over SSH for smaller repeat deploys.

Recommended first automation:

- Web-only deploy for `main` and `local-dev`.
- Helper deploy as manual dispatch or a separate workflow once helper service behavior is proven.

Implemented first automation scaffold:

- Added `.github/workflows/deploy-vps-web.yml` alongside the existing GitHub Pages workflow.
- Pushes to `main`/`master` deploy the `main` slot at `https://xivcraftarchitect.com`.
- Pushes to `local-dev` deploy the `local-dev` slot at `https://dev.xivcraftarchitect.com`.
- Manual dispatch can deploy either `main` or `local-dev`.
- The workflow publishes the Blazor WebAssembly app, rewrites `wwwroot/appsettings.json` to the target domain's hosted API base address, packages `wwwroot`, uploads it over SSH, extracts to `/srv/craftarchitect/web/<slot>/releases/<run>`, and updates `/srv/craftarchitect/web/<slot>/current`.
- The existing GitHub Pages workflow remains present only as a moved notice after the VPS workflow has been proven from GitHub Actions.
- Required GitHub repository secrets:
  - `VPS_HOST`
  - `VPS_USER`
  - `VPS_SSH_PRIVATE_KEY`
  - SSH port is fixed at `22` in the first workflow.
- GitHub Actions deploy key:
  - Local private key path: `C:\Users\gianf\.ssh\xivcraftarchitect_github_actions`
  - Public key is installed in `/home/ubuntu/.ssh/authorized_keys` on the VPS.
  - Fingerprint: `SHA256:R4OzKPxQg6FNnQumpEnKEiBNTghoLlsGus4m9RJpDNE`
- Repository secrets were added to `FranFkntastic/XIV-Craft-Architect` on 2026-06-22:
  - `VPS_HOST`
  - `VPS_USER`
  - `VPS_SSH_PRIVATE_KEY`
- Rollback remains a symlink operation: repoint `/srv/craftarchitect/web/<slot>/current` to an earlier directory under `/srv/craftarchitect/web/<slot>/releases/`.
- First GitHub Actions VPS deployment proof:
  - `main` push deployment succeeded on run `27990725568` attempt `3`.
  - `local-dev` manual dispatch deployment succeeded on run `27991063085` attempt `1`.
  - `https://xivcraftarchitect.com/appsettings.json` returned `https://xivcraftarchitect.com/api/`.
  - `https://dev.xivcraftarchitect.com/appsettings.json` returned `https://dev.xivcraftarchitect.com/api/`.
  - `/srv/craftarchitect/web/main/current` points at release `27990725568-3-7063bc9`.
  - `/srv/craftarchitect/web/local-dev/current` points at release `27991063085-1-7063bc9`.
- Cleanup/hardening follow-up:
  - `.github/workflows/deploy-web.yml` is now a manual GitHub Pages moved-notice deploy.
  - `.github/workflows/deploy-vps-web.yml` is the canonical automatic web deployment path.
  - `.github/workflows/deploy-vps-lodestone.yml` deploys the Lodestone helper manually as a self-contained Linux service release.
  - Helper releases are installed under `/srv/craftarchitect/services/lodestone/releases/<run>`.
  - Helper rollback remains a symlink operation: repoint `/srv/craftarchitect/services/lodestone/current`, then restart `craftarchitect-lodestone`.
  - The first helper workflow run, `27992340708`, successfully installed release `27992340708-1-0950606` but reported failure because the readiness check raced the systemd startup.
  - The helper deploy workflow now waits up to 30 seconds for `http://127.0.0.1:5128/` before declaring failure.
  - The fixed helper workflow run, `27992419520`, succeeded and installed release `27992419520-1-c28bc0b`.
  - Live service verification after run `27992419520`:
    - `systemctl is-active craftarchitect-lodestone` returned `active`.
    - `/srv/craftarchitect/services/lodestone/current` pointed to `/srv/craftarchitect/services/lodestone/releases/27992419520-1-c28bc0b`.
    - `http://127.0.0.1:5128/` returned the helper ready payload.
    - `https://dev.xivcraftarchitect.com/api/lodestone/crafters/search?name=Level%20Checker&world=Behemoth` returned character id `16331040`.

Verification:

- Push or manually dispatch workflow.
- Confirm `current` symlink changes on the VPS.
- Confirm both domains serve the new build.
- Confirm rollback can be done by repointing `current` to a previous release.

Go/no-go:

- Keep the old GitHub Pages URL useful as a manually dispatched moved notice after the VPS workflow has deployed successfully at least once.

### Phase 5: Retire GitHub Pages App Deployment

Goal:

Stop publishing the app to GitHub Pages once the VPS path is trusted.

Work:

1. Replace `.github/workflows/deploy-web.yml` or add a new workflow and disable the old one.
2. Remove GitHub Pages permissions if no longer needed:

   ```yaml
   pages: write
   id-token: write
   ```

3. Remove Pages artifact assembly:

   ```text
   dist/pages
   .nojekyll
   /local-dev path copy
   404.html copy for Pages
   ```

4. Update documentation/runbook to identify the VPS as the canonical deployment target.
5. Decide whether to leave GitHub Pages configured as a moved notice for old links.

Implemented cleanup:

- GitHub Pages is retained as an old-link notice target, but it is no longer an app host or rollback target.
- The Pages workflow is manually dispatched only and publishes static moved notices instead of `main` and `local-dev` app builds.
- The VPS web workflow remains automatic for `main`/`local-dev` web changes.
- The Lodestone helper workflow is manually dispatched so backend service restarts are deliberate.

Verification:

- GitHub Actions no longer deploys to Pages on `main`/`local-dev`.
- VPS deploy workflow is the only automatic web deployment path.
- App remains reachable at both VPS domains.

### Phase 6: Post-Migration Hardening

Goal:

Make the new host safer and easier to maintain.

Work:

1. Confirm SSH key login works reliably. Done locally through the `craftarchitect-vps` SSH alias.
2. Consider disabling password SSH login after backup access is confirmed.
3. Add basic log commands to the runbook:

   ```bash
   journalctl -u caddy -n 100 --no-pager
   journalctl -u craftarchitect-lodestone -n 100 --no-pager
   ```

4. Add release cleanup policy, such as keeping the last 5 releases per environment.
5. Decide whether to enable Cloudflare proxy mode with SSL/TLS set to Full (strict).
6. Add uptime checks for:
   - `https://xivcraftarchitect.com`
   - `https://dev.xivcraftarchitect.com`
   - `https://xivcraftarchitect.com/api/lodestone/...`

Completed hardening:

- GitHub Pages automatic deployment is disabled; Pages is manual moved notice only.
- VPS web deployment is automatic for the hosted web slots.
- Lodestone helper deployment is manual and release-based.
- Web releases and helper releases keep only the newest five releases per slot/service.
- Helper deployment performs a localhost readiness check before verifying the public route.
- Focused workflow/config tests cover the VPS web workflow, helper workflow, and manual Pages moved-notice workflow.

### Known Risks / Watch Items

- `appsettings.json` currently uses an absolute localhost helper URL.
- Relative `BaseAddress` may require code adjustment in `Program.cs`.
- GitHub Pages path logic in `index.html` is legacy behavior that should not control VPS routing.
- Browser IndexedDB is origin-scoped; users will not automatically carry data from GitHub Pages to the new domain.
- `main` and `local-dev` currently have different commits and possibly different data schemas.
- The helper uses NetStone and public Lodestone HTML-backed behavior, so explicit failure logging remains important.
- Ubuntu 26.04 means framework-dependent .NET 8 hosting is not the default server path.

## Initial Recommendation

Use:

```text
Domain/DNS: Cloudflare
VPS:        OVHcloud VPS-1 unless checkout pricing or account friction changes the math
Server:     Ubuntu 26.04
Web:        Caddy
Backend:    ASP.NET Core LodestoneLookup as self-contained systemd service
Docker:     Defer until the app has more services or branch isolation needs
```

Reason:

OVHcloud VPS-1 is currently the best budget/breathing-room balance. It is cheap enough to justify over static-only hosting, but large enough that the first hosted helper and future small services will not immediately run into 1 GB RAM constraints.
