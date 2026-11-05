import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
import venv
from unittest.mock import patch


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPEC = importlib.util.spec_from_file_location(
    "fenton_platform", os.path.join(ROOT, "tools", "fenton-platform.py")
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FentonPlatformWrapperTests(unittest.TestCase):
    def test_profile_maps_fst_components_and_preserves_safety_authority(self):
        with open(os.path.join(ROOT, ".fenton", "repo.json"), encoding="utf-8") as handle:
            profile = json.load(handle)
        with open(os.path.join(ROOT, ".fenton", "policy.json"), encoding="utf-8") as handle:
            policy = json.load(handle)
        self.assertIn("service", profile["components"])
        self.assertIn("web", profile["components"])
        self.assertIn("shared", profile["dependencies"]["service"])
        self.assertIn("secret-scan", profile["recipes"])
        self.assertEqual(["node", "--test", "tools/secret-scan.test.mjs"],
                         profile["recipes"]["secret-scan"]["argv"])
        self.assertEqual(["tools/secret-scan.test.mjs"],
                         profile["recipes"]["secret-scan"]["selectors"])
        self.assertEqual([], profile["recipes"]["secret-scan"]["resources"])
        self.assertTrue(policy["trusted_root_required"])
        self.assertFalse(policy["authority"]["production"])
        self.assertFalse(policy["authority"]["destructive"])
        self.assertFalse(policy["authority"]["provider_calls"])

    @unittest.skipUnless(importlib.util.find_spec("fenton_tooling"),
                         "installed Fenton tooling is unavailable")
    def test_installed_sdk_builds_secret_scan_plan_from_profile(self):
        from fenton_tooling.config import load_json
        from fenton_tooling.runners import plan_from_recipe

        profile = load_json(os.path.join(ROOT, ".fenton", "repo.json"))
        plan = plan_from_recipe(profile, "secret-scan", ROOT)
        self.assertEqual(("node", "--test", "tools/secret-scan.test.mjs"),
                         plan.command)
        self.assertEqual("repository-tools", plan.component)
        self.assertEqual(("tools/secret-scan.test.mjs",), plan.selectors)
        self.assertEqual("node", plan.platform)
        self.assertEqual(60.0, plan.timeout_seconds)
        self.assertEqual((), plan.resources)

    def test_run_forwards_recipe_without_shell_or_private_path(self):
        with patch.object(
            MODULE.subprocess,
            "run",
            return_value=subprocess.CompletedProcess([], 0),
        ) as run:
            self.assertEqual(0, MODULE.main(["run", "--recipe", "secret-scan"]))
        command = run.call_args.args[0]
        self.assertEqual(
            [MODULE.sys.executable, "-I", "-m", "fenton_tooling",
             "run", "--recipe", "secret-scan", "--repo-root", ROOT],
            command,
        )
        self.assertNotIn("PYTHONPATH", run.call_args.kwargs)

    def test_select_rejects_traversal_before_invoking_tool(self):
        with patch.object(MODULE.subprocess, "run") as run:
            self.assertEqual(2, MODULE.main(["select", "../outside"]))
        run.assert_not_called()

    def test_invalid_recipe_is_rejected_before_invoking_tool(self):
        with patch.object(MODULE.subprocess, "run") as run:
            self.assertEqual(2, MODULE.main(["run", "--recipe", "../secret"]))
        run.assert_not_called()

    def test_missing_optional_tool_fails_clearly(self):
        with patch.object(MODULE.subprocess, "run",
                          side_effect=FileNotFoundError("missing")):
            with patch.object(MODULE.sys, "stderr") as stderr:
                self.assertEqual(2, MODULE.main(["doctor"]))
                self.assertIn("optional fenton_tooling is unavailable",
                              "".join(call.args[0] for call in stderr.write.call_args_list))

    def test_root_profile_must_be_inside_a_real_fixture(self):
        with tempfile.TemporaryDirectory() as root:
            os.mkdir(os.path.join(root, ".fenton"))
            for name in ("repo.json", "policy.json"):
                open(os.path.join(root, ".fenton", name), "w").close()
            with patch.object(
                MODULE.subprocess,
                "run",
                return_value=subprocess.CompletedProcess([], 0),
            ) as run:
                self.assertEqual(0, MODULE.main(["--root", root, "doctor"]))
            self.assertEqual(root, run.call_args.kwargs["cwd"])

    @unittest.skipUnless(hasattr(os, "symlink"), "symlinks unavailable")
    def test_profile_directory_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = os.path.join(temp, "repo")
            outside = os.path.join(temp, "outside")
            os.makedirs(outside)
            os.makedirs(root)
            os.symlink(outside, os.path.join(root, ".fenton"))
            with patch.object(MODULE.subprocess, "run") as run:
                self.assertEqual(2, MODULE.main(["--root", root, "doctor"]))
            run.assert_not_called()

    def test_installed_tool_smoke_ignores_candidate_module_shadow(self):
        with tempfile.TemporaryDirectory() as temp:
            root = os.path.join(temp, "repo")
            os.makedirs(os.path.join(root, ".fenton"))
            for name in ("repo.json", "policy.json"):
                with open(os.path.join(root, ".fenton", name), "w", encoding="utf-8") as handle:
                    handle.write("{}")
            os.makedirs(os.path.join(root, "fenton_tooling"))
            with open(os.path.join(root, "fenton_tooling", "__main__.py"), "w",
                      encoding="utf-8") as handle:
                handle.write("raise SystemExit(91)\n")
            interpreter = os.path.join(temp, "venv")
            venv.EnvBuilder(with_pip=False, clear=True).create(interpreter)
            site_packages = next(
                os.path.join(interpreter, "lib", name, "site-packages")
                for name in os.listdir(os.path.join(interpreter, "lib"))
                if name.startswith("python")
            )
            package = os.path.join(site_packages, "fenton_tooling")
            os.makedirs(package)
            with open(os.path.join(package, "__main__.py"), "w", encoding="utf-8") as handle:
                handle.write(
                    "import sys\n"
                    "assert sys.argv[1:] == ['doctor']\n"
                    "print('trusted shim')\n"
                )
            self.assertEqual(0, MODULE.main(["--root", root, "--python",
                                             os.path.join(interpreter, "bin", "python"),
                                             "doctor"]))

    def test_missing_module_returns_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            root = os.path.join(temp, "repo")
            os.makedirs(os.path.join(root, ".fenton"))
            for name in ("repo.json", "policy.json"):
                open(os.path.join(root, ".fenton", name), "w").close()
            interpreter = os.path.join(temp, "venv")
            venv.EnvBuilder(with_pip=False, clear=True).create(interpreter)
            self.assertNotEqual(0, MODULE.main([
                "--root", root, "--python", os.path.join(interpreter, "bin", "python"),
                "doctor"
            ]))


if __name__ == "__main__":
    unittest.main()
