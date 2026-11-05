#!/usr/bin/env python3
"""Optional boundary wrapper for the separately installed Fenton tooling."""

import argparse
import os
import re
import subprocess
import sys


_RECIPE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")


def _repository_root(value):
    root = os.path.abspath(value or os.getcwd())
    if not os.path.isdir(root):
        raise ValueError("repository root is not a directory: " + root)
    current = root
    while True:
        if os.path.islink(current):
            raise ValueError("repository root may not be a symlink")
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    profile_dir = os.path.join(root, ".fenton")
    if os.path.islink(profile_dir):
        raise ValueError("profile directory may not be a symlink")
    resolved_root = os.path.realpath(root)
    for relative in (".fenton/repo.json", ".fenton/policy.json"):
        path = os.path.join(root, relative)
        if os.path.islink(path):
            raise ValueError("profile paths may not be symlinks")
        if not os.path.isfile(path):
            raise ValueError("missing repository profile: " + relative)
        if os.path.commonpath((os.path.realpath(path), resolved_root)) != resolved_root:
            raise ValueError("profile path escapes repository root")
    return root


def _relative_path(value):
    if not value or os.path.isabs(value):
        raise ValueError("changed paths must be relative")
    normalized = value.replace("\\", "/")
    parts = normalized.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise ValueError("changed paths may not contain traversal")
    return normalized


def _tool_command(arguments, interpreter):
    if not arguments:
        raise ValueError("optional fenton_tooling is not installed")
    return [interpreter, "-I", "-m", "fenton_tooling", *arguments]


def _invoke(root, arguments, interpreter):
    try:
        return subprocess.run(_tool_command(arguments, interpreter), cwd=root, check=False)
    except (FileNotFoundError, ModuleNotFoundError) as exc:
        raise RuntimeError(
            "optional fenton_tooling is unavailable; install the approved "
            "first-party package before using this wrapper"
        ) from exc


def _parser():
    parser = argparse.ArgumentParser(prog="fenton-platform")
    parser.add_argument("--root", default=None, help=argparse.SUPPRESS)
    parser.add_argument("--python", default=os.environ.get(
        "FENTON_TOOLING_PYTHON", sys.executable), help=argparse.SUPPRESS)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("doctor")
    select = sub.add_parser("select")
    select.add_argument("paths", nargs="+")
    run = sub.add_parser("run")
    run.add_argument("--recipe", required=True)
    return parser


def main(argv=None):
    args = _parser().parse_args(argv)
    try:
        root = _repository_root(args.root)
        if args.command == "select":
            paths = [_relative_path(path) for path in args.paths]
            return _invoke(root, ["select", *paths], args.python).returncode
        if args.command == "run":
            if not _RECIPE.fullmatch(args.recipe):
                raise ValueError("invalid recipe name")
            return _invoke(root, ["run", "--recipe", args.recipe, "--repo-root", root],
                           args.python).returncode
        return _invoke(root, ["doctor"], args.python).returncode
    except (RuntimeError, ValueError) as exc:
        print("fenton-platform: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
