#!/usr/bin/env python3
"""Rewrite Craft Architect helper routes without crossing deploy ownership."""

from __future__ import annotations

import argparse
from pathlib import Path
from typing import Iterable


PRODUCTION_HOST = "xivcraftarchitect.com"
DEVELOPMENT_HOST = "dev.xivcraftarchitect.com"

LEGACY_PREFIXES = (
    "/api/profile-host",
    "/api/trade",
    "/api/xivdata/commission-briefs",
    "/api/lodestone",
    "/api/xivdata",
    "/api/craft",
    "/api/discord",
)

# Keep the commission route before the broad XIV data route. Caddy path matchers
# are specificity-aware, but the generated source should carry the authority
# order directly instead of relying on adapter reordering.
MANAGED_PREFIXES = (
    "/api/profile-host",
    "/api/trade",
    "/api/xivdata/commission-briefs",
    "/api/identity",
    "/api/lodestone",
    "/api/xivdata",
    "/api/craft",
    "/api/discord",
)

CANONICAL_PORT = "5128"
STAGING_PORT = "5129"


class RouteRewriteError(ValueError):
    """Raised when the current Caddy authority cannot be changed safely."""


def _matching_close(lines: list[str], start: int) -> int:
    depth = 0
    for index in range(start, len(lines)):
        depth += lines[index].count("{") - lines[index].count("}")
        if depth == 0:
            return index
    raise RouteRewriteError(f"Unclosed Caddy block at line {start + 1}.")


def _find_site(lines: list[str], host: str) -> tuple[int, int]:
    anchors = [
        index
        for index, line in enumerate(lines)
        if line.rstrip("\r\n") == f"{host} {{"
    ]
    if len(anchors) != 1:
        raise RouteRewriteError(f"Expected one Caddy site block for {host}.")
    start = anchors[0]
    return start, _matching_close(lines, start)


def _managed_prefix(line: str) -> str | None:
    parts = line.strip().split()
    if len(parts) != 3 or parts[0] != "handle" or parts[2] != "{":
        return None
    prefix = parts[1].removesuffix("*")
    known = set(LEGACY_PREFIXES) | set(MANAGED_PREFIXES)
    return prefix if prefix in known else None


def _strip_managed_routes(
    lines: list[str], site_start: int, site_end: int, host: str
) -> list[str]:
    cursor = site_start + 1
    retained: list[str] = []
    seen: set[str] = set()
    while cursor < site_end:
        prefix = _managed_prefix(lines[cursor])
        if prefix is None:
            retained.append(lines[cursor])
            cursor += 1
            continue
        if prefix in seen:
            raise RouteRewriteError(
                f"Duplicate managed Caddy route for {host} {prefix}."
            )
        seen.add(prefix)
        cursor = _matching_close(lines, cursor) + 1
        if cursor < site_end and not lines[cursor].strip():
            cursor += 1

    legacy = set(LEGACY_PREFIXES)
    current = set(MANAGED_PREFIXES)
    if seen not in (legacy, current):
        missing_legacy = sorted(legacy - seen)
        missing_current = sorted(current - seen)
        raise RouteRewriteError(
            f"Managed Caddy routes for {host} are incomplete or mixed. "
            f"Missing legacy routes: {missing_legacy}; "
            f"missing current routes: {missing_current}."
        )
    return retained


def _render_routes(port_by_prefix: dict[str, str]) -> str:
    return "".join(
        f"\thandle {prefix}* {{\n"
        "\t\turi strip_prefix /api\n"
        f"\t\treverse_proxy 127.0.0.1:{port_by_prefix[prefix]}\n"
        "\t}\n\n"
        for prefix in MANAGED_PREFIXES
    )


def _all_on(port: str) -> dict[str, str]:
    return {prefix: port for prefix in MANAGED_PREFIXES}


def _development_split() -> dict[str, str]:
    routes = _all_on(STAGING_PORT)
    for prefix in (
        "/api/profile-host",
        "/api/trade",
        "/api/xivdata/commission-briefs",
        "/api/identity",
    ):
        routes[prefix] = CANONICAL_PORT
    return routes


def _requested_sites(
    target: str, canonicalized: bool
) -> tuple[tuple[str, dict[str, str]], ...]:
    if target == "canonical":
        sites: list[tuple[str, dict[str, str]]] = [
            (PRODUCTION_HOST, _all_on(CANONICAL_PORT))
        ]
        if canonicalized:
            sites.append((DEVELOPMENT_HOST, _development_split()))
        return tuple(sites)
    if target == "staging":
        routes = _development_split() if canonicalized else _all_on(STAGING_PORT)
        return ((DEVELOPMENT_HOST, routes),)
    raise RouteRewriteError(f"Unsupported helper target: {target}.")


def rewrite_caddy_routes(text: str, target: str, canonicalized: bool) -> str:
    lines = text.splitlines(keepends=True)
    if not lines:
        raise RouteRewriteError("Caddy configuration is empty.")

    requested = _requested_sites(target, canonicalized)
    sites: list[tuple[int, int, str, dict[str, str], list[str]]] = []
    for host, routes in requested:
        site_start, site_end = _find_site(lines, host)
        retained = _strip_managed_routes(lines, site_start, site_end, host)
        sites.append((site_start, site_end, host, routes, retained))

    # Replace from the bottom so the original indexes stay valid. Validation for
    # every requested site has already succeeded, making a multi-site cutover
    # one file write instead of a partially applied mutation.
    for site_start, site_end, _host, routes, retained in sorted(
        sites, key=lambda item: item[0], reverse=True
    ):
        lines[site_start + 1 : site_end] = [_render_routes(routes), *retained]
    return "".join(lines)


def _parse_args(arguments: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--caddyfile", required=True, type=Path)
    parser.add_argument("--target", required=True, choices=("canonical", "staging"))
    parser.add_argument("--canonicalized", required=True, choices=("true", "false"))
    return parser.parse_args(arguments)


def main(arguments: Iterable[str] | None = None) -> int:
    args = _parse_args(arguments)
    with args.caddyfile.open("r", encoding="utf-8", newline="") as stream:
        original = stream.read()
    rewritten = rewrite_caddy_routes(
        original,
        target=args.target,
        canonicalized=args.canonicalized == "true",
    )
    with args.caddyfile.open("w", encoding="utf-8", newline="") as stream:
        stream.write(rewritten)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
