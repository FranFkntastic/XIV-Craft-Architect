from __future__ import annotations

import re
import sys
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPT_DIRECTORY))

from rewrite_caddy_helper_routes import (  # noqa: E402
    CANONICAL_PORT,
    DEVELOPMENT_HOST,
    LEGACY_PREFIXES,
    MANAGED_PREFIXES,
    PRE_JOIN_PREFIXES,
    PRODUCTION_HOST,
    RouteRewriteError,
    STAGING_PORT,
    rewrite_caddy_routes,
)


def route_block(prefix: str, port: str) -> str:
    return (
        f"\thandle {prefix}* {{\n"
        "\t\turi strip_prefix /api\n"
        f"\t\treverse_proxy 127.0.0.1:{port}\n"
        "\t}\n\n"
    )


def site(host: str, port: str, prefixes: tuple[str, ...] = LEGACY_PREFIXES) -> str:
    routes = "".join(route_block(prefix, port) for prefix in prefixes)
    return (
        f"{host} {{\n"
        "\tencode zstd gzip\n\n"
        f"{routes}"
        "\thandle {\n"
        "\t\troot * /srv/craftarchitect/web\n"
        "\t\tfile_server\n"
        "\t}\n"
        "}\n"
    )


def fixture(production_port: str = "5998", development_port: str = "5999") -> str:
    return (
        "# global content must survive byte-for-byte\n"
        f"{site(PRODUCTION_HOST, production_port)}\n"
        f"{site(DEVELOPMENT_HOST, development_port)}"
    )


def site_text(text: str, host: str) -> str:
    lines = text.splitlines(keepends=True)
    start = next(index for index, line in enumerate(lines) if line.rstrip() == f"{host} {{")
    depth = 0
    for index in range(start, len(lines)):
        depth += lines[index].count("{") - lines[index].count("}")
        if depth == 0:
            return "".join(lines[start : index + 1])
    raise AssertionError(f"Unclosed fixture site for {host}.")


def route_ports(text: str, host: str) -> dict[str, str]:
    current = site_text(text, host)
    matches = re.findall(
        r"handle (/[^* ]+)\* \{.*?reverse_proxy 127\.0\.0\.1:(\d+).*?\}",
        current,
        flags=re.DOTALL,
    )
    return dict(matches)


class RewriteCaddyHelperRoutesTests(unittest.TestCase):
    def test_deployed_routes_gain_exact_join_proxy_once(self) -> None:
        original = site(PRODUCTION_HOST, "5128", PRE_JOIN_PREFIXES) + site(
            DEVELOPMENT_HOST, "5129", PRE_JOIN_PREFIXES
        )
        rewritten = rewrite_caddy_routes(original, "canonical", canonicalized=True)
        for host, port in ((PRODUCTION_HOST, "5128"), (DEVELOPMENT_HOST, "5129")):
            block = site_text(rewritten, host)
            self.assertIn(f"handle /join/* {{\n\t\treverse_proxy 127.0.0.1:{port}\n\t}}", block)
            self.assertEqual(len(PRE_JOIN_PREFIXES), block.count("uri strip_prefix /api"))
        self.assertEqual(rewritten, rewrite_caddy_routes(rewritten, "canonical", True))

        mixed = site(PRODUCTION_HOST, "5128", (*LEGACY_PREFIXES, "/join/"))
        with self.assertRaises(RouteRewriteError):
            rewrite_caddy_routes(mixed, "canonical", False)

    def test_canonical_cutover_keeps_each_domain_on_its_own_helper(self) -> None:
        rewritten = rewrite_caddy_routes(fixture(), "canonical", canonicalized=True)

        self.assertEqual(
            {prefix: CANONICAL_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, PRODUCTION_HOST),
        )
        self.assertEqual(
            {prefix: STAGING_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, DEVELOPMENT_HOST),
        )
        dev = site_text(rewritten, DEVELOPMENT_HOST)
        self.assertLess(
            dev.index("handle /api/xivdata/commission-briefs*"),
            dev.index("handle /api/xivdata*"),
        )
        self.assertNotIn("handle /api/craft/plans*", rewritten)
        self.assertIn("handle /api/craft*", rewritten)

    def test_staging_after_cutover_preserves_production_and_dev_isolation(self) -> None:
        original = fixture()
        production_before = site_text(original, PRODUCTION_HOST)

        rewritten = rewrite_caddy_routes(original, "staging", canonicalized=True)

        self.assertEqual(production_before, site_text(rewritten, PRODUCTION_HOST))
        self.assertEqual(
            {prefix: STAGING_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, DEVELOPMENT_HOST),
        )


if __name__ == "__main__":
    unittest.main()
