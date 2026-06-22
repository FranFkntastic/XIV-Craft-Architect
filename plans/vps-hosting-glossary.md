# VPS Hosting Glossary

Date: 2026-06-22
Project: **FFXIV Craft Architect**

## VPS

A Virtual Private Server is a rented Linux computer on the internet.

For this migration, the VPS is the remote machine that will host the web app and Lodestone helper.

Current VPS IPv4:

```text
51.222.141.175
```

## IPv4

An IPv4 address is the older/common internet address format:

```text
51.222.141.175
```

It is like the street address of the VPS.

## IPv6

An IPv6 address is the newer, longer internet address format:

```text
2607:5300:229:4e4::1
```

It is also a server address, but IPv4 is enough for the first deployment. IPv6 can be configured later.

## Domain

A domain is the human-friendly name:

```text
xivcraftarchitect.com
```

People use the domain. Computers ultimately route to an IP address.

## DNS

DNS is the phone book that translates domain names into IP addresses.

For example:

```text
xivcraftarchitect.com -> 51.222.141.175
```

## A Record

An A record is a DNS record that points a name to an IPv4 address.

```text
A  @  51.222.141.175
```

This means the root domain points to the VPS IPv4 address.

## AAAA Record

An AAAA record points a name to an IPv6 address.

The first deployment can skip AAAA records until IPv6 is confirmed on the VPS.

## @ In DNS

In Cloudflare DNS, `@` means the root domain.

```text
@ = xivcraftarchitect.com
```

## Subdomain

A subdomain is a name before the root domain.

```text
dev.xivcraftarchitect.com
```

For this project, subdomains are useful because browser storage is separated by origin:

```text
xivcraftarchitect.com      = production storage
dev.xivcraftarchitect.com  = development storage
```

## DNS Only vs Proxied

Cloudflare can either only answer DNS or also proxy traffic.

DNS only:

```text
Browser -> VPS
```

Proxied:

```text
Browser -> Cloudflare -> VPS
```

Start with DNS only so Caddy can prove HTTPS directly and setup has fewer moving parts. After the VPS works, Cloudflare proxy mode can be evaluated.

## SSH

SSH is the secure remote terminal login.

```powershell
ssh ubuntu@51.222.141.175
```

This logs into the VPS at `51.222.141.175` as the `ubuntu` user.

SSH password prompts intentionally show no characters, dots, or cursor movement while typing.

## ubuntu User

`ubuntu` is the initial Linux account created by the VPS image.

It is the user account, not the operating system itself.

## sudo

`sudo` runs a command with administrator privileges.

```bash
sudo apt update
```

Linux makes administrator power explicit instead of giving every command full control.

## apt

`apt` is Ubuntu's package manager.

It installs and updates software packages from configured repositories.

```bash
sudo apt install caddy
```

## Package

A package is installable software managed by the operating system.

Examples:

```text
caddy
curl
unzip
ufw
```

## Caddy

Caddy is the planned web server and reverse proxy.

It will:

- serve the Blazor WebAssembly static files
- reverse-proxy Lodestone API requests to the helper
- manage HTTPS certificates automatically

## Web Server

A web server receives browser requests.

When someone visits:

```text
https://xivcraftarchitect.com
```

Caddy will receive the request and answer it.

## Reverse Proxy

A reverse proxy receives a public request and forwards it to an internal service.

Example:

```text
Browser asks:
https://xivcraftarchitect.com/api/lodestone/crafters/search

Caddy forwards internally to:
http://127.0.0.1:5128/lodestone/crafters/search
```

The helper remains private while Caddy acts as the public front door.

## localhost / 127.0.0.1

`127.0.0.1` means "this same machine."

If the Lodestone helper listens on:

```text
127.0.0.1:5128
```

it is reachable only from inside the VPS.

## Port

A port is a numbered door on a computer.

Common ports for this migration:

```text
22    SSH
80    HTTP
443   HTTPS
5128  Lodestone helper, internal only
```

## Firewall

A firewall controls which ports outsiders can reach.

The first deployment should allow:

```text
22   SSH
80   HTTP
443  HTTPS
```

Most other public ports should stay blocked.

## UFW

UFW means Uncomplicated Firewall.

It is Ubuntu's friendly firewall command-line tool.

```bash
sudo ufw allow 443/tcp
```

## HTTPS

HTTPS is encrypted web traffic.

```text
http://  = not encrypted
https:// = encrypted
```

Caddy can automatically get and renew HTTPS certificates.

## Certificate

A certificate proves that the server is allowed to serve a domain.

Browsers require certificates for HTTPS.

## Blazor WebAssembly

Blazor WebAssembly is the frontend web app.

From the server's perspective, it publishes static files:

```text
HTML
CSS
JavaScript
.wasm
.dll
assets
```

Caddy can serve these files directly.

## Static Files

Static files are files the web server can hand to the browser as-is.

The Blazor WebAssembly frontend is static from the server's point of view.

## ASP.NET Core Helper

The ASP.NET Core helper is the backend Lodestone service:

```text
FFXIV Craft Architect.LodestoneLookup
```

It runs on the server, talks to Lodestone through NetStone, and returns search/preview results to the browser.

## systemd

systemd is Linux's service manager.

It starts, stops, restarts, and monitors background services.

Examples:

```bash
sudo systemctl status caddy
sudo systemctl restart caddy
```

## Service

A service is a background program managed by systemd.

Caddy is a service. The Lodestone helper will eventually become a service too.

## Self-Contained Publish

A self-contained .NET publish bundles the .NET runtime with the app.

This matters because:

```text
FFXIV Craft Architect targets .NET 8.
Ubuntu 26.04 ships with .NET 10.
```

A self-contained Lodestone helper avoids needing to install .NET 8 on the VPS for the first deployment.

## CORS

CORS is a browser security rule about whether one website can call another website's API.

Using a same-origin route like:

```text
https://xivcraftarchitect.com/api/lodestone/...
```

avoids most CORS trouble.

## Origin

An origin is roughly:

```text
scheme + domain + port
```

Example:

```text
https://xivcraftarchitect.com
```

Browsers use origins for storage and API security decisions.

## IndexedDB

IndexedDB is browser-side storage.

**FFXIV Craft Architect** uses it for local plans, settings, market cache, and Trade data.

IndexedDB is origin-scoped, so these do not share storage:

```text
https://franfkntastic.github.io
https://xivcraftarchitect.com
https://dev.xivcraftarchitect.com
```

Moving domains means existing browser-local data will not appear automatically. Export/import remains important.

## Target Shape

The first hosted architecture:

```text
Browser
  -> https://xivcraftarchitect.com
      -> Caddy
          -> Blazor static files

Browser
  -> https://xivcraftarchitect.com/api/lodestone/...
      -> Caddy
          -> Lodestone helper on 127.0.0.1:5128
              -> Lodestone
```

Plain English:

Caddy is the public front desk. The Blazor app is the public website. The Lodestone helper is a private back-office worker. DNS tells people where the building is. SSH lets us enter the building to set it up.

