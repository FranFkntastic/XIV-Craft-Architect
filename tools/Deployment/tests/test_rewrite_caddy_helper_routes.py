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
        r"handle (/api/[^* ]+)\* \{.*?reverse_proxy 127\.0\.0\.1:(\d+).*?\}",
        current,
        flags=re.DOTALL,
    )
    return dict(matches)


class RewriteCaddyHelperRoutesTests(unittest.TestCase):
    def test_staging_before_cutover_owns_only_dev_and_keeps_every_route_staging(self) -> None:
        original = fixture()
        production_before = site_text(original, PRODUCTION_HOST)

        rewritten = rewrite_caddy_routes(original, "staging", canonicalized=False)

        self.assertEqual(production_before, site_text(rewritten, PRODUCTION_HOST))
        self.assertEqual(
            {prefix: STAGING_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, DEVELOPMENT_HOST),
        )
        self.assertIn("root * /srv/craftarchitect/web", rewritten)

    def test_canonical_cutover_installs_production_and_dev_authority_atomically(self) -> None:
        rewritten = rewrite_caddy_routes(fixture(), "canonical", canonicalized=True)

        self.assertEqual(
            {prefix: CANONICAL_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, PRODUCTION_HOST),
        )
        expected_dev = {prefix: STAGING_PORT for prefix in MANAGED_PREFIXES}
        expected_dev.update(
            {
                "/api/profile-host": CANONICAL_PORT,
                "/api/trade": CANONICAL_PORT,
                "/api/xivdata/commission-briefs": CANONICAL_PORT,
            }
        )
        self.assertEqual(expected_dev, route_ports(rewritten, DEVELOPMENT_HOST))
        dev = site_text(rewritten, DEVELOPMENT_HOST)
        self.assertLess(
            dev.index("handle /api/xivdata/commission-briefs*"),
            dev.index("handle /api/xivdata*"),
        )
        self.assertNotIn("handle /api/craft/plans*", rewritten)
        self.assertIn("handle /api/craft*", rewritten)

    def test_canonical_before_cutover_leaves_dev_site_untouched(self) -> None:
        original = fixture()
        development_before = site_text(original, DEVELOPMENT_HOST)

        rewritten = rewrite_caddy_routes(original, "canonical", canonicalized=False)

        self.assertEqual(development_before, site_text(rewritten, DEVELOPMENT_HOST))
        self.assertEqual(
            {prefix: CANONICAL_PORT for prefix in MANAGED_PREFIXES},
            route_ports(rewritten, PRODUCTION_HOST),
        )

    def test_staging_after_cutover_preserves_production_and_dev_split(self) -> None:
        original = fixture()
        production_before = site_text(original, PRODUCTION_HOST)

        rewritten = rewrite_caddy_routes(original, "staging", canonicalized=True)

        self.assertEqual(production_before, site_text(rewritten, PRODUCTION_HOST))
        self.assertEqual(CANONICAL_PORT, route_ports(rewritten, DEVELOPMENT_HOST)["/api/trade"])
        self.assertEqual(STAGING_PORT, route_ports(rewritten, DEVELOPMENT_HOST)["/api/discord"])

    def test_rewrite_is_idempotent(self) -> None:
        first = rewrite_caddy_routes(fixture(), "canonical", canonicalized=True)
        second = rewrite_caddy_routes(first, "canonical", canonicalized=True)

        self.assertEqual(first, second)

    def test_missing_duplicate_and_mixed_anchors_fail_closed(self) -> None:
        cases = {
            "missing host": fixture().replace(f"{DEVELOPMENT_HOST} {{", "missing.example.com {"),
            "duplicate host": fixture() + "\n" + site(DEVELOPMENT_HOST, STAGING_PORT),
            "missing route": fixture().replace(route_block("/api/trade", "5999"), "", 1),
            "duplicate route": fixture().replace(
                route_block("/api/trade", "5999"),
                route_block("/api/trade", "5999") * 2,
                1,
            ),
        }
        for name, value in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(RouteRewriteError):
                    rewrite_caddy_routes(value, "staging", canonicalized=True)

    def test_workflow_owns_cutover_and_retires_staging_credentials(self) -> None:
        workflow = (
            Path(__file__).resolve().parents[3]
            / ".github"
            / "workflows"
            / "deploy-vps-lodestone.yml"
        ).read_text(encoding="utf-8")

        self.assertIn("reconsolidate_profile_host:", workflow)
        self.assertIn("import-active-credentials", workflow)
        self.assertIn("profile-host-canonicalized-v1", workflow)
        self.assertIn('runtime_profile_host_enabled="false"', workflow)
        self.assertIn('cutover_complete="true"', workflow)
        self.assertLess(
            workflow.index("import-active-credentials"),
            workflow.index('cutover_complete="true"'),
        )
        self.assertLess(
            workflow.index("sudo systemctl reload caddy", workflow.index("import-active-credentials")),
            workflow.index('cutover_complete="true"'),
        )
        self.assertNotIn("vars.CRAFT_ARCHITECT_PROFILE_HOST_CANONICALIZED", workflow)


if __name__ == "__main__":
    unittest.main()
