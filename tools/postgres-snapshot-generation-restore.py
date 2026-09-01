#!/usr/bin/env python3
"""Exact archived snapshot-child restore after a committed drop."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone


TOOL_ID = "fst.snapshot-generation-restore.v1"
SCHEMA_VERSION = 1
REQUIRED_BASE = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence"
)
IDENTIFIER = re.compile(r"^[a-z_][a-z0-9_]*$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
OPERATION_ID = re.compile(r"^[0-9a-f]{32}$")
CANONICAL_UNSIGNED_DECIMAL = re.compile(
    r"^(?:0|[1-9][0-9]*)$"
)
POSTGRES_OID_MAX = (1 << 32) - 1
VALIDATOR_BASE_TOOL_SHA256 = (
    "acb358604d9f642da3d4809581328f761"
    "18cb912c32765353b8594cc68a1522d"
)
REPAIR_PACKAGE_TOOL_ID = (
    "fst.snapshot-generation-restore-tool-repair-package.v1"
)
REPAIR_PACKAGE_FILES = {
    "restore-tool.py",
    "postgres-snapshot-generation-archive.py",
    "source-manifest.json",
    "pinned-to-base.patch",
    "base-to-final.patch",
    "test-evidence/manifest.json",
    "test-evidence/results.json",
    "repair-manifest.json",
}


class RestoreError(RuntimeError):
    pass


def load_archive_module():
    path = pathlib.Path(__file__).with_name(
        "postgres-snapshot-generation-archive.py"
    )
    specification = importlib.util.spec_from_file_location(
        "fst_snapshot_generation_archive",
        path,
    )
    if specification is None or specification.loader is None:
        raise RestoreError("archive helper module could not be loaded")
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


ARCHIVE = load_archive_module()


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def sha256_path(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_bytes(value):
    return (
        json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        + "\n"
    ).encode("utf-8")


def stable_hash(value):
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def sql_nullable_literal(value):
    return (
        "NULL"
        if value is None
        else ARCHIVE.sql_literal(value)
    )


def validate_actor(value, label):
    if (
        not isinstance(value, str)
        or not value.strip()
        or len(value) > 512
        or any(
            ord(character) < 32
            or ord(character) == 127
            for character in value
        )
    ):
        raise RestoreError(f"{label} is invalid")
    return value


def seal_report(value):
    sealed = dict(value)
    sealed.pop("reportSha256", None)
    sealed["reportSha256"] = stable_hash(sealed)
    return sealed


def write_new_json(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(
        path,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(canonical_bytes(value))
        handle.flush()
        os.fsync(handle.fileno())


def write_new_bytes(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(
        path,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(value)
        handle.flush()
        os.fsync(handle.fileno())


def validate_path(path, *, exists=True, directory=False):
    value = pathlib.Path(path)
    resolved_base = REQUIRED_BASE.resolve(strict=True)
    resolved = value.resolve(strict=exists)
    if resolved != resolved_base and resolved_base not in resolved.parents:
        raise RestoreError(
            f"restore path must remain below {resolved_base}: {resolved}"
        )
    current = resolved_base
    relative = resolved.relative_to(resolved_base)
    for part in relative.parts:
        current = current / part
        if not current.exists():
            continue
        if current.is_symlink():
            raise RestoreError(f"restore path contains a symbolic link: {current}")
    if exists:
        if directory and not resolved.is_dir():
            raise RestoreError(f"restore directory is missing: {resolved}")
        if not directory and not resolved.is_file():
            raise RestoreError(f"restore file is missing: {resolved}")
    elif resolved.exists():
        raise RestoreError(f"restore output already exists: {resolved}")
    return resolved


def load_json(path):
    return json.loads(pathlib.Path(path).read_text(encoding="utf-8"))


def reject_duplicate_json_keys(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise RestoreError(
                f"canonical JSON contains duplicate key: {key}"
            )
        value[key] = item
    return value


def reject_json_constant(value):
    raise RestoreError(
        f"canonical JSON constant is invalid: {value}"
    )


def scan_json_string_end(value, offset):
    if offset >= len(value) or value[offset] != 0x22:
        raise RestoreError("canonical JSON string is invalid")
    index = offset + 1
    while index < len(value):
        current = value[index]
        if current == 0x22:
            return index + 1
        if current < 0x20:
            raise RestoreError(
                "canonical JSON string contains a control byte"
            )
        if current == 0x5C:
            index += 1
            if index >= len(value):
                break
            escaped = value[index]
            if escaped == 0x75:
                if (
                    index + 4 >= len(value)
                    or any(
                        item
                        not in b"0123456789abcdefABCDEF"
                        for item in value[
                            index + 1:index + 5
                        ]
                    )
                ):
                    raise RestoreError(
                        "canonical JSON unicode escape is invalid"
                    )
                index += 5
                continue
            if escaped not in b'"\\/bfnrt':
                raise RestoreError(
                    "canonical JSON escape is invalid"
                )
        index += 1
    raise RestoreError("canonical JSON string is unterminated")


def reject_json_whitespace(value):
    index = 0
    while index < len(value):
        if value[index] == 0x22:
            index = scan_json_string_end(value, index)
            continue
        if value[index] in b" \t\r\n":
            raise RestoreError(
                "canonical JSON contains noncanonical whitespace"
            )
        index += 1


def scan_json_value_end(value, offset):
    if offset >= len(value):
        raise RestoreError("canonical JSON value is missing")
    if value[offset] == 0x22:
        return scan_json_string_end(value, offset)
    if value[offset] in (0x7B, 0x5B):
        stack = [
            0x7D if value[offset] == 0x7B else 0x5D
        ]
        index = offset + 1
        while index < len(value):
            current = value[index]
            if current == 0x22:
                index = scan_json_string_end(value, index)
                continue
            if current in (0x7B, 0x5B):
                stack.append(
                    0x7D if current == 0x7B else 0x5D
                )
            elif current in (0x7D, 0x5D):
                if not stack or current != stack[-1]:
                    raise RestoreError(
                        "canonical JSON nesting is invalid"
                    )
                stack.pop()
                if not stack:
                    return index + 1
            index += 1
        raise RestoreError(
            "canonical JSON composite is unterminated"
        )
    index = offset
    while (
        index < len(value)
        and value[index] not in (0x2C, 0x7D)
    ):
        index += 1
    if index == offset:
        raise RestoreError("canonical JSON value is missing")
    return index


def dotnet_ordinal_key(value):
    return value.encode("utf-16-be", "surrogatepass")


def read_canonical_csharp_object(
    path,
    identity_names,
    label,
):
    file_bytes = pathlib.Path(path).read_bytes()
    if not file_bytes.endswith(b"\n"):
        raise RestoreError(
            f"{label} canonical file terminator is missing"
        )
    source = file_bytes[:-1]
    try:
        text = source.decode("utf-8")
        plan = json.loads(
            text,
            object_pairs_hook=reject_duplicate_json_keys,
            parse_constant=reject_json_constant,
        )
    except (
        UnicodeDecodeError,
        json.JSONDecodeError,
        RestoreError,
    ) as error:
        if isinstance(error, RestoreError):
            raise
        raise RestoreError(
            f"{label} JSON is malformed"
        ) from error
    if not isinstance(plan, dict):
        raise RestoreError(
            f"{label} JSON root must be an object"
        )
    if (
        len(source) < 2
        or source[0] != 0x7B
        or source[-1] != 0x7D
    ):
        raise RestoreError(
            f"{label} JSON has leading or trailing data"
        )
    reject_json_whitespace(source)

    members = []
    index = 1
    while index < len(source) - 1:
        member_start = index
        key_end = scan_json_string_end(source, index)
        try:
            key = json.loads(
                source[index:key_end].decode("utf-8")
            )
        except (
            UnicodeDecodeError,
            json.JSONDecodeError,
        ) as error:
            raise RestoreError(
                f"{label} top-level key is invalid"
            ) from error
        if (
            not isinstance(key, str)
            or source[index:key_end]
            != ARCHIVE.dotnet_canonical_json_bytes(key)
            or key_end >= len(source)
            or source[key_end] != 0x3A
        ):
            raise RestoreError(
                f"{label} top-level key is not canonical"
            )
        value_end = scan_json_value_end(
            source,
            key_end + 1,
        )
        members.append(
            {
                "name": key,
                "start": member_start,
                "value_start": key_end + 1,
                "end": value_end,
            }
        )
        index = value_end
        if index == len(source) - 1:
            break
        if source[index] != 0x2C:
            raise RestoreError(
                f"{label} top-level delimiter is invalid"
            )
        index += 1
        if index == len(source) - 1:
            raise RestoreError(
                f"{label} JSON has a trailing comma"
            )
    if index != len(source) - 1:
        raise RestoreError(
            f"{label} JSON has trailing data"
        )
    names = [member["name"] for member in members]
    if (
        len(names) != len(set(names))
        or names != sorted(
            names,
            key=dotnet_ordinal_key,
        )
    ):
        raise RestoreError(
            f"{label} top-level properties are not unique and ordinal-sorted"
        )
    if any(
        names.count(identity_name) != 1
        for identity_name in identity_names
    ):
        raise RestoreError(
            f"{label} top-level identity is incomplete"
        )
    for identity_name in identity_names:
        identity = next(
            member
            for member in members
            if member["name"] == identity_name
        )
        if source[
            identity["value_start"]:identity["end"]
        ] != ARCHIVE.dotnet_canonical_json_bytes(
            plan[identity_name]
        ):
            raise RestoreError(
                f"{label} top-level identity encoding is not canonical"
            )
    unsigned_members = [
        source[member["start"]:member["end"]]
        for member in members
        if member["name"] not in identity_names
    ]
    unsigned = (
        b"{"
        + b",".join(unsigned_members)
        + b"}"
    )
    return plan, unsigned


def validate_drop_plan(path, expected_digest=None, expected_operation=None):
    plan, unsigned = read_canonical_csharp_object(
        path,
        ("planDigest", "dropOperationId"),
        "drop plan",
    )
    computed_digest = hashlib.sha256(unsigned).hexdigest()
    operation_canonical = (
        b'{"planDigest":"'
        + computed_digest.encode("ascii")
        + b'","toolId":"fst.snapshot-generation-drop-only.v1"}'
    )
    computed_operation = hashlib.sha256(
        operation_canonical
    ).hexdigest()[:32]
    if (
        plan.get("schemaVersion") != 1
        or plan.get("toolId") != "fst.snapshot-generation-drop-only.v1"
        or plan.get("explicitApprovalRequired") is not True
        or not SHA256.fullmatch(str(plan.get("planDigest", "")))
        or not OPERATION_ID.fullmatch(str(plan.get("dropOperationId", "")))
        or plan["planDigest"] != computed_digest
        or plan["dropOperationId"] != computed_operation
    ):
        raise RestoreError("drop plan contract is invalid")
    if expected_digest and plan["planDigest"] != expected_digest:
        raise RestoreError("drop plan digest differs from expected")
    if expected_operation and plan["dropOperationId"] != expected_operation:
        raise RestoreError("drop operation differs from expected")
    if plan["activePlan"]["archive"]["instrument"] == "Solo_Bass" and int(
        plan["activePlan"]["archive"]["snapshotId"]
    ) == 1308:
        raise RestoreError("Solo Bass snapshot 1308 is permanently excluded")
    return plan


def validate_drop_report(path, plan):
    report, unsigned = read_canonical_csharp_object(
        path,
        ("reportSha256",),
        "drop report",
    )
    report_hash = report.get("reportSha256")
    target = plan["activePlan"]["archive"]
    validate_actor(report.get("actor"), "drop-report actor")
    validate_actor(
        report.get("reference"),
        "drop-report reference",
    )
    committed_action = (
        report.get("action") == "drop"
        and report.get("commitOutcome")
        in (
            "committed",
            "reconciled-committed",
            "already-committed",
        )
    ) or (
        report.get("action") == "confirm"
        and report.get("commitOutcome") == "confirmed"
    )
    if (
        report.get("schemaVersion") != 1
        or report.get("toolId") != "fst.snapshot-generation-drop-only.v1"
        or not committed_action
        or report.get("dropOperationId") != plan["dropOperationId"]
        or report.get("planDigest") != plan["planDigest"]
        or report.get("status") != "dropped"
        or report.get("instrument") != target["instrument"]
        or int(report.get("snapshotId", 0))
        != int(target["snapshotId"])
        or int(report.get("childOid", 0))
        != int(target["childOid"])
        or int(report.get("childRelfilenode", 0))
        != int(target["childRelfilenode"])
        or int(report.get("rowCount", 0))
        != int(target["rowCount"])
        or report.get("rowFingerprintSha256")
        != target["rowFingerprintSha256"]
        or not SHA256.fullmatch(str(report_hash or ""))
        or hashlib.sha256(unsigned).hexdigest()
        != report_hash
    ):
        raise RestoreError("drop report is invalid")
    return report


def validate_bundle(path, expected_manifest_sha):
    root = validate_path(path, directory=True)
    manifest_path = root / "bundle-manifest.json"
    checksum_path = root / "SHA256SUMS"
    if (
        sha256_path(manifest_path) != expected_manifest_sha
        or not checksum_path.is_file()
    ):
        raise RestoreError("recovery bundle manifest is invalid")
    checksums = {}
    for line in checksum_path.read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._/-]+)", line)
        if match is None or match.group(2) in checksums:
            raise RestoreError("recovery bundle checksum inventory is malformed")
        checksums[match.group(2)] = match.group(1)
    observed = {}
    for item in root.rglob("*"):
        if item.is_symlink():
            raise RestoreError(f"recovery bundle contains a symlink: {item}")
        if item.is_file() and item.stat().st_mode & 0o222:
            raise RestoreError(
                f"recovery bundle file is writable: {item}"
            )
        if item.is_file() and item != checksum_path:
            relative = item.relative_to(root).as_posix()
            observed[relative] = sha256_path(item)
    if checksums != observed:
        raise RestoreError("recovery bundle checksum inventory differs")
    return root, load_json(manifest_path)


def validate_repair_package(
    path,
    expected_manifest_sha=None,
):
    root = validate_path(path, directory=True)
    manifest_path = root / "repair-manifest.json"
    checksum_path = root / "SHA256SUMS"
    if (
        not manifest_path.is_file()
        or not checksum_path.is_file()
        or (
            expected_manifest_sha is not None
            and sha256_path(manifest_path)
            != expected_manifest_sha
        )
    ):
        raise RestoreError(
            "restore-tool repair package manifest is invalid"
        )
    checksums = {}
    for line in checksum_path.read_text(
        encoding="utf-8"
    ).splitlines():
        match = re.fullmatch(
            r"([0-9a-f]{64})  ([A-Za-z0-9._/-]+)",
            line,
        )
        if match is None or match.group(2) in checksums:
            raise RestoreError(
                "restore-tool repair checksum inventory is malformed"
            )
        checksums[match.group(2)] = match.group(1)
    observed = {}
    for item in root.rglob("*"):
        if item.is_symlink():
            raise RestoreError(
                f"restore-tool repair package contains a symlink: {item}"
            )
        if item.is_file() and item.stat().st_mode & 0o222:
            raise RestoreError(
                f"restore-tool repair package file is writable: {item}"
            )
        if item.is_file() and item != checksum_path:
            observed[
                item.relative_to(root).as_posix()
            ] = sha256_path(item)
    if (
        set(observed) != REPAIR_PACKAGE_FILES
        or checksums != observed
    ):
        raise RestoreError(
            "restore-tool repair package file set differs"
        )
    manifest = load_json(manifest_path)
    if (
        manifest.get("schemaVersion") != 1
        or manifest.get("toolId")
        != REPAIR_PACKAGE_TOOL_ID
        or manifest.get("status") != "accepted"
        or manifest.get("validatorBaseToolSha256")
        != VALIDATOR_BASE_TOOL_SHA256
        or manifest.get("authorizedRestoreToolSha256")
        != checksums["restore-tool.py"]
        or manifest.get("authorizedArchiveHelperSha256")
        != checksums[
            "postgres-snapshot-generation-archive.py"
        ]
        or manifest.get("sourceManifestSha256")
        != checksums["source-manifest.json"]
        or manifest.get("pinnedToBaseDiffSha256")
        != checksums["pinned-to-base.patch"]
        or manifest.get("baseToFinalDiffSha256")
        != checksums["base-to-final.patch"]
        or manifest.get("testEvidenceManifestSha256")
        != checksums["test-evidence/manifest.json"]
    ):
        raise RestoreError(
            "restore-tool repair package contract is invalid"
        )
    return root, manifest


def restore_tool_authorization_lookup_sql(
    drop_plan,
    authorization_id,
):
    if not OPERATION_ID.fullmatch(
        str(authorization_id or "")
    ):
        raise RestoreError(
            "restore-tool authorization ID is invalid"
        )
    return f"""
        SELECT json_build_object(
            'authorizationId',
                auth_row.authorization_id,
            'dropOperationId',
                auth_row.drop_operation_id,
            'dropPlanDigest',
                auth_row.drop_plan_digest,
            'originalBundleManifestSha256',
                auth_row.original_bundle_manifest_sha256,
            'pinnedRestoreToolSha256',
                auth_row.pinned_restore_tool_sha256,
            'validatorBaseToolSha256',
                auth_row.validator_base_tool_sha256,
            'authorizedRestoreToolSha256',
                auth_row.authorized_restore_tool_sha256,
            'authorizedArchiveHelperSha256',
                auth_row.authorized_archive_helper_sha256,
            'authorizerBinarySha256',
                auth_row.authorizer_binary_sha256,
            'repairPackageManifestSha256',
                auth_row.repair_package_manifest_sha256,
            'repositoryCommit',
                auth_row.repository_commit,
            'repositoryTreeId',
                auth_row.repository_tree_id,
            'pinnedToBaseDiffSha256',
                auth_row.pinned_to_base_diff_sha256,
            'baseToFinalDiffSha256',
                auth_row.base_to_final_diff_sha256,
            'sourceManifestSha256',
                auth_row.source_manifest_sha256,
            'testEvidenceManifestSha256',
                auth_row.test_evidence_manifest_sha256,
            'reasonCode', auth_row.reason_code,
            'reasonText', auth_row.reason_text,
            'approvedBy', auth_row.approved_by,
            'reviewedBy', auth_row.reviewed_by,
            'approvalReference',
                auth_row.approval_reference,
            'evidenceSha256',
                auth_row.evidence_sha256,
            'canonicalEvidenceDbSha256',
                auth_row.canonical_evidence_db_sha256,
            'authorizedAt',
                auth_row.authorized_at)
        FROM
            snapshot_generation_restore_tool_authorizations
                auth_row
        WHERE auth_row.authorization_id =
                {ARCHIVE.sql_literal(authorization_id)}
          AND auth_row.drop_operation_id =
                {ARCHIVE.sql_literal(drop_plan["dropOperationId"])}
          AND auth_row.drop_plan_digest =
                {ARCHIVE.sql_literal(drop_plan["planDigest"])}
    """


def read_restore_tool_authorization(
    args,
    drop_plan,
    authorization_id,
):
    sql = restore_tool_authorization_lookup_sql(
        drop_plan,
        authorization_id,
    )
    authorization = ARCHIVE.psql_json(
        args.source_container,
        args.pg_user,
        args.pg_database,
        sql,
    )
    if not isinstance(authorization, dict):
        raise RestoreError(
            "restore-tool authorization is unavailable"
        )
    try:
        authorized_at = datetime.fromisoformat(
            str(authorization["authorizedAt"])
            .replace("Z", "+00:00")
        )
        age = datetime.now(timezone.utc) - authorized_at
        if age.total_seconds() > 24 * 60 * 60:
            print(
                "WARNING: restore-tool authorization "
                f"is {age.total_seconds() / 3600:.1f} hours old",
                file=sys.stderr,
            )
    except (
        KeyError,
        TypeError,
        ValueError,
    ) as error:
        raise RestoreError(
            "restore-tool authorization timestamp is invalid"
        ) from error
    return authorization


def derive_restore_tool_authorization_id(
    authorization,
):
    fields = [
        "fst.snapshot-generation-restore-tool-authorization.v1",
        authorization["dropOperationId"],
        authorization["dropPlanDigest"],
        authorization["originalBundleManifestSha256"],
        authorization["pinnedRestoreToolSha256"],
        authorization["validatorBaseToolSha256"],
        authorization["authorizedRestoreToolSha256"],
        authorization["authorizedArchiveHelperSha256"],
        authorization["authorizerBinarySha256"],
        authorization["repairPackageManifestSha256"],
        authorization["repositoryCommit"],
        authorization["repositoryTreeId"],
        authorization["pinnedToBaseDiffSha256"],
        authorization["baseToFinalDiffSha256"],
        authorization["sourceManifestSha256"],
        authorization["testEvidenceManifestSha256"],
        authorization["evidenceSha256"],
        authorization["canonicalEvidenceDbSha256"],
    ]
    return hashlib.sha256(
        ":".join(fields).encode("utf-8")
    ).hexdigest()[:32]


def resolve_restore_tool_authorization(
    args,
    drop_plan,
    bundle,
    *,
    authorization_id=None,
    repair_package=None,
    repair_package_manifest_sha256=None,
):
    pinned_sha = drop_plan["restoreToolSha256"]
    executing_sha = sha256_path(__file__)
    bundled_tool_sha = sha256_path(
        bundle / "restore-tool.py")
    bundled_helper_sha = sha256_path(
        bundle
        / "postgres-snapshot-generation-archive.py")
    current_helper = pathlib.Path(__file__).with_name(
        "postgres-snapshot-generation-archive.py")
    current_helper_sha = sha256_path(current_helper)
    if bundled_tool_sha != pinned_sha:
        raise RestoreError(
            "original recovery bundle restore tool differs from the drop pin"
        )
    if executing_sha == pinned_sha:
        if (
            authorization_id is not None
            or repair_package is not None
            or repair_package_manifest_sha256
                is not None
            or current_helper_sha != bundled_helper_sha
        ):
            raise RestoreError(
                "pinned restore tool cannot use repair authorization"
            )
        return {
            "mode": "pinned",
            "authorizationId": None,
            "pinnedToolSha256": pinned_sha,
            "executingToolSha256": executing_sha,
            "validatorBaseToolSha256": None,
            "authorizedArchiveHelperSha256": None,
            "repairPackagePath": None,
            "repairPackageManifestSha256": None,
        }
    if (
        authorization_id is None
        or repair_package is None
        or repair_package_manifest_sha256 is None
    ):
        raise RestoreError(
            "replacement restore tool requires explicit authorization"
        )
    package, repair = validate_repair_package(
        repair_package,
        repair_package_manifest_sha256,
    )
    if (
        repair["dropOperationId"]
        != drop_plan["dropOperationId"]
        or repair["dropPlanDigest"]
        != drop_plan["planDigest"]
        or repair["originalBundleManifestSha256"]
        != drop_plan["recoveryBundleManifestSha256"]
        or repair["pinnedRestoreToolSha256"]
        != pinned_sha
        or repair["authorizedRestoreToolSha256"]
        != executing_sha
        or repair["authorizedArchiveHelperSha256"]
        != bundled_helper_sha
        or repair["authorizedArchiveHelperSha256"]
        != current_helper_sha
        or sha256_path(package / "restore-tool.py")
        != executing_sha
        or sha256_path(
            package
            / "postgres-snapshot-generation-archive.py")
        != current_helper_sha
    ):
        raise RestoreError(
            "restore-tool repair package differs from the executing tool or original bundle"
        )
    authorization = read_restore_tool_authorization(
        args,
        drop_plan,
        authorization_id,
    )
    if derive_restore_tool_authorization_id(
        authorization
    ) != authorization_id:
        raise RestoreError(
            "restore-tool authorization ID is not bound to database evidence"
        )
    comparisons = {
        "authorizationId": authorization_id,
        "dropOperationId":
            drop_plan["dropOperationId"],
        "dropPlanDigest": drop_plan["planDigest"],
        "originalBundleManifestSha256":
            drop_plan["recoveryBundleManifestSha256"],
        "pinnedRestoreToolSha256": pinned_sha,
        "validatorBaseToolSha256":
            repair["validatorBaseToolSha256"],
        "authorizedRestoreToolSha256":
            executing_sha,
        "authorizedArchiveHelperSha256":
            current_helper_sha,
        "repairPackageManifestSha256":
            repair_package_manifest_sha256,
        "repositoryCommit":
            repair["repositoryCommit"],
        "repositoryTreeId":
            repair["repositoryTreeId"],
        "pinnedToBaseDiffSha256":
            repair["pinnedToBaseDiffSha256"],
        "baseToFinalDiffSha256":
            repair["baseToFinalDiffSha256"],
        "sourceManifestSha256":
            repair["sourceManifestSha256"],
        "testEvidenceManifestSha256":
            repair["testEvidenceManifestSha256"],
        "authorizerBinarySha256":
            repair["authorizerBinarySha256"],
    }
    if any(
        authorization.get(key) != value
        for key, value in comparisons.items()
    ):
        raise RestoreError(
            "restore-tool authorization differs from repair provenance"
        )
    return {
        "mode": "authorized",
        **comparisons,
        "pinnedToolSha256": pinned_sha,
        "executingToolSha256": executing_sha,
        "repairPackagePath": str(package),
        "authorizationEvidenceSha256":
            authorization["evidenceSha256"],
        "authorizationDbEvidenceSha256":
            authorization[
                "canonicalEvidenceDbSha256"],
        "authorizedAt":
            authorization["authorizedAt"],
    }


def validate_route_pair(baseline_path, candidate_path):
    validate_capture_checksums(
        pathlib.Path(baseline_path).parent)
    validate_capture_checksums(
        pathlib.Path(candidate_path).parent)
    baseline = load_json(baseline_path)
    candidate = load_json(candidate_path)
    if (
        int(baseline.get("publicationId", 0))
        != int(candidate.get("publicationId", 0))
        or int(baseline.get("publishedScrapeId", 0))
        != int(candidate.get("publishedScrapeId", 0))
    ):
        raise RestoreError("route captures use different publications")
    left = {entry["name"]: entry for entry in baseline.get("entries", [])}
    right = {entry["name"]: entry for entry in candidate.get("entries", [])}
    if len(left) != 55 or set(left) != set(right):
        raise RestoreError("route captures do not contain the same 55 routes")
    for name in sorted(left):
        for manifest, entry, root in (
            (baseline, left[name], pathlib.Path(baseline_path).parent),
            (candidate, right[name], pathlib.Path(candidate_path).parent),
        ):
            raw = root / "raw" / f"{name}.body"
            if (
                not raw.is_file()
                or raw.is_symlink()
                or raw.stat().st_size != int(entry["bytes"])
                or sha256_path(raw) != entry["rawSha256"]
                or int(entry["curlExit"]) != 0
            ):
                raise RestoreError(f"route body integrity failed: {name}")
            if entry["isJson"]:
                normalized = root / "normalized" / f"{name}.json"
                if (
                    not normalized.is_file()
                    or normalized.is_symlink()
                    or sha256_path(normalized) != entry["semanticSha256"]
                ):
                    raise RestoreError(
                        f"normalized route integrity failed: {name}"
                    )
        left_entry = left[name]
        right_entry = right[name]
        if any(
            left_entry[key] != right_entry[key]
            for key in ("method", "path", "status", "contentType", "isJson")
        ):
            raise RestoreError(f"route contract differs: {name}")
        comparison_key = "semanticSha256" if left_entry["isJson"] else "rawSha256"
        if left_entry[comparison_key] != right_entry[comparison_key]:
            raise RestoreError(f"route content differs: {name}")
    return {
        "baselineManifestPath": str(pathlib.Path(baseline_path).resolve()),
        "baselineManifestSha256": sha256_path(baseline_path),
        "candidateManifestPath": str(pathlib.Path(candidate_path).resolve()),
        "candidateManifestSha256": sha256_path(candidate_path),
        "publicationId": int(baseline["publicationId"]),
        "publishedScrapeId": int(baseline["publishedScrapeId"]),
        "routeCount": 55,
        "statusParity": True,
        "semanticJsonParity": True,
        "differenceCount": 0,
    }


def validate_capture_checksums(directory):
    checksum_path = directory / "SHA256SUMS"
    if not checksum_path.is_file() or checksum_path.is_symlink():
        raise RestoreError(
            f"route checksum inventory is missing: {directory}"
        )
    expected = {}
    for line in checksum_path.read_text(
        encoding="utf-8"
    ).splitlines():
        match = re.fullmatch(
            r"([0-9a-f]{64})  ([A-Za-z0-9._/-]+)",
            line,
        )
        if match is None or match.group(2) in expected:
            raise RestoreError(
                "route checksum inventory is malformed"
            )
        expected[match.group(2)] = match.group(1)
    observed = {}
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise RestoreError(
                f"route evidence contains a symbolic link: {path}"
            )
        if path.is_file() and path != checksum_path:
            observed[path.relative_to(directory).as_posix()] = (
                sha256_path(path)
            )
    if expected != observed:
        raise RestoreError(
            "route checksum inventory differs"
        )


def child_catalog(manifest, catalog):
    child_name = manifest["target"]["childRelation"]
    matches = [
        item
        for item in catalog["physicalCatalog"]
        if item.get("name") == child_name
    ]
    if len(matches) != 1:
        raise RestoreError("archive child catalog is not unique")
    return matches[0]


def parse_postgres_oid(
    value,
    field_name,
    *,
    allow_zero=False,
):
    if isinstance(value, bool):
        text = None
    elif isinstance(value, int):
        text = str(value)
    elif isinstance(value, str):
        text = value
    else:
        text = None
    if (
        text is None
        or CANONICAL_UNSIGNED_DECIMAL.fullmatch(text)
            is None
    ):
        raise RestoreError(
            f"{field_name} is not a canonical "
            f"{'unsigned' if allow_zero else 'positive'} "
            "PostgreSQL OID"
        )
    parsed = int(text)
    if (
        parsed > POSTGRES_OID_MAX
        or (parsed == 0 and not allow_zero)
    ):
        raise RestoreError(
            f"{field_name} is not a canonical "
            f"{'unsigned' if allow_zero else 'positive'} "
            "PostgreSQL OID"
        )
    return parsed


def normalize_postgres_oid_array(
    value,
    field_name,
    *,
    allow_zero=False,
):
    if not isinstance(value, list):
        raise RestoreError(
            f"{field_name} is not a PostgreSQL OID array"
        )
    return [
        parse_postgres_oid(
            item,
            f"{field_name}[{index}]",
            allow_zero=allow_zero,
        )
        for index, item in enumerate(value)
    ]


def validate_supported_index_specs(child):
    indexes = child.get("indexes") or []
    primary = [
        index for index in indexes
        if index.get("isPrimary")
    ]
    score = [
        index for index in indexes
        if not index.get("isPrimary")
    ]
    expected = {
        "pk": {
            "columns": [
                "snapshot_id",
                "song_id",
                "instrument",
                "account_id",
            ],
            "suffix": (
                "USING btree "
                "(snapshot_id, song_id, instrument, account_id)"
            ),
            "unique": True,
        },
        "score": {
            "columns": [
                "snapshot_id",
                "song_id",
                "instrument",
                "score",
            ],
            "suffix": (
                "USING btree "
                "(snapshot_id, song_id, instrument, score DESC)"
            ),
            "unique": False,
        },
    }
    if len(primary) != 1 or len(score) != 1:
        raise RestoreError(
            "archive must contain exactly one PK and score index"
        )
    result = {"pk": primary[0], "score": score[0]}
    for role, index in result.items():
        definition = " ".join(
            str(index.get("definition", "")).split()
        )
        specification = expected[role]
        expected_options = (
            [0, 0, 0, 0]
            if role == "pk"
            else [0, 0, 0, 3]
        )
        # The archive/proof contract is pinned to PostgreSQL 17.
        expected_opclasses = (
            [3124, 3126, 3126, 3126]
            if role == "pk"
            else [3124, 3126, 3126, 1978]
        )
        expected_collations = (
            [0, 100, 100, 100]
            if role == "pk"
            else [0, 100, 100, 0]
        )
        observed_opclasses = (
            normalize_postgres_oid_array(
                index["opclassOids"],
                f"archive {role} index opclassOids",
            )
            if "opclassOids" in index
            else expected_opclasses
        )
        observed_collations = (
            normalize_postgres_oid_array(
                index["collationOids"],
                f"archive {role} index collationOids",
                allow_zero=True,
            )
            if "collationOids" in index
            else expected_collations
        )
        if (
            index.get("columnNames")
            != specification["columns"]
            or index.get("accessMethod") != "btree"
            or index.get("tablespaceName")
            != "pg_default"
            or index.get("isValid") is not True
            or index.get("isReady") is not True
            or index.get("isUnique")
            is not specification["unique"]
            or bool(index.get("isPrimary"))
            is not (role == "pk")
            or not definition.endswith(
                specification["suffix"]
            )
            or " WHERE " in definition.upper()
            or " INCLUDE " in definition.upper()
            or " COLLATE " in definition.upper()
            or index.get("expressions") not in (None, "")
            or index.get("predicate") not in (None, "")
            or index.get("relationOptions") not in (None, [])
            or (
                "indNKeyAtts" in index
                and (
                    type(index["indNKeyAtts"]) is not int
                    or index["indNKeyAtts"] != 4
                )
            )
            or (
                "indNAtts" in index
                and (
                    type(index["indNAtts"]) is not int
                    or index["indNAtts"] != 4
                )
            )
            or (
                "keyAttnums" in index
                and (
                    not isinstance(
                        index["keyAttnums"],
                        list,
                    )
                    or len(index["keyAttnums"]) != 4
                    or any(
                        type(value) is not int
                        or value <= 0
                        for value in index["keyAttnums"]
                    )
                )
            )
            or observed_opclasses != expected_opclasses
            or observed_collations != expected_collations
            or (
                "indOptions" in index
                and (
                    not isinstance(
                        index["indOptions"],
                        list,
                    )
                    or any(
                        type(value) is not int
                        for value in index["indOptions"]
                    )
                    or index["indOptions"]
                    != expected_options
                )
            )
        ):
            raise RestoreError(
                f"archive {role} index is outside the fixed supported shape"
            )
    return result


def logical_index_shape_sha256(child, root, top):
    supported = validate_supported_index_specs(child)
    root_indexes = {
        parse_postgres_oid(
            index["indexOid"],
            "archive root indexOid",
        ): index
        for index in root.get("indexes") or []
    }
    top_indexes = {
        parse_postgres_oid(
            index["indexOid"],
            "archive top indexOid",
        ): index
        for index in top.get("indexes") or []
    }
    projection = []
    for role in ("pk", "score"):
        index = supported[role]
        parent_root_oid = parse_postgres_oid(
            index["parentIndexOid"],
            f"archive {role} parentIndexOid",
        )
        root_index = root_indexes.get(parent_root_oid)
        if root_index is None:
            raise RestoreError(
                f"restored {role} root index link is missing"
            )
        parent_top_oid = parse_postgres_oid(
            root_index["parentIndexOid"],
            f"archive {role} parentTopIndexOid",
        )
        top_index = top_indexes.get(parent_top_oid)
        if top_index is None:
            raise RestoreError(
                f"restored {role} top index link is missing"
            )
        expected_top = (
            "leaderboard_entries_snapshot_pkey"
            if role == "pk"
            else "ix_les_snapshot_song_score"
        )
        if top_index["indexName"] != expected_top:
            raise RestoreError(
                f"restored {role} top index role is invalid"
            )
        for property_name in (
            "indNKeyAtts",
            "indNAtts",
            "keyAttnums",
            "opclassOids",
            "collationOids",
            "indOptions",
        ):
            values = []
            for value in (
                index,
                root_index,
                top_index,
            ):
                if property_name not in value:
                    continue
                observed = value[property_name]
                if property_name in (
                    "opclassOids",
                    "collationOids",
                ):
                    observed = normalize_postgres_oid_array(
                        observed,
                        f"restored {role} {property_name}",
                        allow_zero=(
                            property_name
                            == "collationOids"
                        ),
                    )
                else:
                    observed = json.dumps(
                        observed,
                        sort_keys=True,
                        separators=(",", ":"),
                    )
                values.append(observed)
            if values and (
                len(values) != 3
                or values[0] != values[1]
                or values[0] != values[2]
            ):
                raise RestoreError(
                    f"restored {role} {property_name} "
                    "differs in its attachment chain"
                )
        projection.append(
            {
                "role": role,
                "primary": role == "pk",
                "unique": role == "pk",
                "valid": True,
                "ready": True,
                "accessMethod": "btree",
                "tablespaceName": "pg_default",
                "keyColumns": index["columnNames"],
                "sortDirections": (
                    ["asc", "asc", "asc", "asc"]
                    if role == "pk"
                    else ["asc", "asc", "asc", "desc"]
                ),
                "nullsOrder": (
                    ["last", "last", "last", "last"]
                    if role == "pk"
                    else ["last", "last", "last", "first"]
                ),
                "opclasses": ["default"] * 4,
                "collations": ["default"] * 4,
                "parentRootIndexOid": parent_root_oid,
                "parentTopIndexOid": parent_top_oid,
                "parentTopRole": role,
            }
        )
    return hashlib.sha256(
        ARCHIVE.dotnet_canonical_json_bytes(projection)
    ).hexdigest()


def load_pinned_archive(package):
    checksums = ARCHIVE.read_checksums(package)
    manifest = load_json(package / "manifest.json")
    catalog = load_json(package / "catalog.json")
    if (
        manifest.get("toolId") != ARCHIVE.TOOL_ID
        or manifest.get("schemaVersion") != ARCHIVE.SCHEMA_VERSION
        or manifest.get("status") != "accepted"
        or manifest.get("archiveOnly") is not True
        or manifest["archive"]["sha256"] != checksums["archive.custom"]
        or manifest["toc"]["sha256"] != checksums["archive.toc"]
        or manifest["catalog"]["sha256"] != checksums["catalog.json"]
        or ARCHIVE.stable_hash(catalog["logicalCatalog"])
        != manifest["catalog"]["logicalSha256"]
    ):
        raise RestoreError("pinned archive package is invalid")
    return manifest, catalog, checksums


def validate_fresh_proof(package, drop_plan, manifest, checksums):
    expected = drop_plan["freshProofManifestSha256"]
    bundle = package.parent
    candidates = list(
        package.glob("proofs/*/proof-manifest.json")
    )
    pinned = (
        bundle
        / "evidence"
        / "fresh-proof"
        / "proof-manifest.json"
    )
    if pinned.is_file():
        candidates.append(pinned)
    matches = [
        path
        for path in candidates
        if path.is_file()
        and not path.is_symlink()
        and sha256_path(path) == expected
    ]
    if not matches:
        raise RestoreError(
            "pinned recovery bundle lacks the exact fresh proof"
        )
    proof_path = sorted(
        matches,
        key=lambda path: str(path),
    )[0]
    proof_checksums_path = proof_path.parent / "SHA256SUMS"
    if (
        not proof_checksums_path.is_file()
        or proof_checksums_path.is_symlink()
    ):
        raise RestoreError(
            "fresh proof checksum inventory is missing"
        )
    proof_checksums = {}
    for line in proof_checksums_path.read_text(
        encoding="utf-8"
    ).splitlines():
        match = re.fullmatch(
            r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)",
            line,
        )
        if match is None or match.group(2) in proof_checksums:
            raise RestoreError(
                "fresh proof checksum inventory is malformed"
            )
        proof_checksums[match.group(2)] = match.group(1)
    observed_proof_files = {
        path.name: sha256_path(path)
        for path in proof_path.parent.iterdir()
        if path.is_file()
        and path.name != "SHA256SUMS"
        and not path.is_symlink()
    }
    if (
        proof_checksums != observed_proof_files
        or proof_checksums.get("proof-manifest.json")
        != expected
        or "cleanup.json" not in proof_checksums
    ):
        raise RestoreError(
            "fresh proof checksum inventory differs"
        )
    proof = load_json(proof_path)
    cleanup = proof.get("cleanup") or {}
    validation = proof.get("validation") or {}
    if (
        proof.get("toolId") != ARCHIVE.TOOL_ID
        or proof.get("schemaVersion") != ARCHIVE.SCHEMA_VERSION
        or proof.get("status") != "accepted"
        or proof.get("archiveOnly") is not True
        or proof.get("networkMode") != "none"
        or int(proof.get("publishedPorts", -1)) != 0
        or proof.get("packageManifestSha256")
        != checksums["manifest.json"]
        or proof.get("archiveSha256")
        != manifest["archive"]["sha256"]
        or any(
            cleanup.get(key) is not True
            for key in (
                "containerAbsenceProven",
                "containerRemoved",
                "ownedVolumesRemoved",
                "pgdataRemoved",
                "scratchRemoved",
            )
        )
        or validation.get("rowFingerprint", {}).get("sha256")
        != manifest["rowFingerprint"]["sha256"]
        or validation.get("rowFingerprint", {}).get("rowCount")
        != manifest["rowFingerprint"]["rowCount"]
        or validation.get("restoredLogicalCatalogSha256")
        != validation.get("expectedLogicalCatalogSha256")
        or proof.get("cleanupEvidence", {}).get("path")
        != "cleanup.json"
        or proof.get("cleanupEvidence", {}).get("sha256")
        != proof_checksums["cleanup.json"]
    ):
        raise RestoreError("fresh pinned restore proof is invalid")
    return proof_path, proof


def select_restore_toc(toc_text, child):
    child_name = child["name"]
    supported = validate_supported_index_specs(child)
    primary = [supported["pk"]]
    secondary = [supported["score"]]
    selected = {}
    for line in toc_text.splitlines():
        entry = parse_toc_entry(line)
        if entry is None:
            continue
        key = None
        if (
            entry["kind"] == "TABLE"
            and entry["schema"] == "public"
            and entry["object"] == child_name
        ):
            key = "table"
        elif (
            entry["kind"] == "TABLE DATA"
            and entry["schema"] == "public"
            and entry["object"] == child_name
        ):
            key = "data"
        elif (
            entry["kind"] == "CONSTRAINT"
            and entry["schema"] == "public"
            and entry["object"] == child_name
            and entry["subobject"]
            == primary[0]["indexName"]
        ):
            key = "primary"
        elif (
            entry["kind"] == "INDEX"
            and entry["schema"] == "public"
            and entry["object"]
            == secondary[0]["indexName"]
        ):
            key = "secondary"
        if key is not None:
            if key in selected:
                raise RestoreError(
                    f"archive TOC has duplicate {key} entry"
                )
            selected[key] = line
    required = {"table", "data", "primary", "secondary"}
    if set(selected) != required:
        missing = sorted(required - set(selected))
        raise RestoreError(
            "archive TOC lacks exact child restore entries: "
            + ", ".join(missing)
        )
    lines = [
        selected["table"],
        selected["data"],
        selected["primary"],
        selected["secondary"],
    ]
    return lines


def parse_toc_entry(line):
    identifier, separator, payload = line.partition(";")
    if not separator or not identifier.isdigit():
        return None
    fields = payload.split()
    if (
        len(fields) < 5
        or not fields[0].isdigit()
        or not fields[1].isdigit()
    ):
        return None
    if fields[2:4] == ["TABLE", "DATA"]:
        if len(fields) < 7:
            return None
        return {
            "kind": "TABLE DATA",
            "schema": fields[4],
            "object": fields[5],
            "subobject": None,
        }
    if fields[2] == "TABLE":
        if len(fields) < 6 or fields[3] == "ATTACH":
            return None
        return {
            "kind": "TABLE",
            "schema": fields[3],
            "object": fields[4],
            "subobject": None,
        }
    if fields[2] == "CONSTRAINT":
        if len(fields) < 7:
            return None
        return {
            "kind": "CONSTRAINT",
            "schema": fields[3],
            "object": fields[4],
            "subobject": fields[5],
        }
    if fields[2] == "INDEX":
        if len(fields) < 6 or fields[3] == "ATTACH":
            return None
        return {
            "kind": "INDEX",
            "schema": fields[3],
            "object": fields[4],
            "subobject": None,
        }
    return None


def detached_logical_child(
    child,
    temporary_check_constraint=None,
):
    value = ARCHIVE.logical_catalog([child])[0]
    value["partitionBound"] = ""
    value["parentSchema"] = None
    value["parentRelation"] = None
    if temporary_check_constraint is not None:
        value["constraints"] = [
            constraint
            for constraint in value["constraints"]
            if constraint.get("name")
            != temporary_check_constraint
        ]
    for index in value["indexes"]:
        index.pop("parentIndexName", None)
    return value


def detached_base_relation(
    child,
    temporary_check_constraint=None,
):
    value = ARCHIVE.logical_catalog([child])[0]
    value["partitionBound"] = ""
    value["parentSchema"] = None
    value["parentRelation"] = None
    value["indexes"] = []
    value["constraints"] = [
        constraint
        for constraint in value["constraints"]
        if constraint.get("type") != "p"
        and constraint.get("name")
        != temporary_check_constraint
    ]
    return value


def database_drop_state(args, plan):
    target = plan["activePlan"]["archive"]
    sql = f"""
        SELECT json_build_object(
            'dropExists', EXISTS (
                SELECT 1
                FROM snapshot_generation_drop_operations
                WHERE drop_operation_id =
                    {ARCHIVE.sql_literal(plan["dropOperationId"])}
                  AND plan_digest =
                    {ARCHIVE.sql_literal(plan["planDigest"])}),
            'restoreExists', EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_operations restore_row
                JOIN snapshot_generation_drop_operations drop_row
                  ON drop_row.drop_operation_id =
                        restore_row.drop_operation_id
                WHERE drop_row.drop_operation_id =
                    {ARCHIVE.sql_literal(plan["dropOperationId"])}),
            'originalExists',
                to_regclass(
                    {ARCHIVE.sql_literal("public." + target["childRelation"])})
                    IS NOT NULL,
            'oldOidExists', EXISTS (
                SELECT 1
                FROM pg_class
                WHERE oid = {int(target["childOid"])}),
            'holdActive', EXISTS (
                SELECT 1
                FROM snapshot_generation_retention_holds hold_row
                JOIN snapshot_generation_drop_operations drop_row
                  ON drop_row.hold_id = hold_row.hold_id
                WHERE drop_row.drop_operation_id =
                    {ARCHIVE.sql_literal(plan["dropOperationId"])}
                  AND hold_row.released_at IS NULL))
    """
    return ARCHIVE.psql_json(
        args.source_container,
        args.pg_user,
        args.pg_database,
        sql,
    )


def require_database_identity(args, drop_plan):
    observed = ARCHIVE.database_identity(
        args.source_container,
        args.pg_user,
        args.pg_database,
    )
    expected = drop_plan["database"]
    if (
        observed["database"] != expected["databaseName"]
        or int(observed["databaseOid"])
        != int(expected["databaseOid"])
        or observed["systemIdentifier"]
        != expected["systemIdentifier"]
        or int(observed["serverVersionNum"])
        != int(expected["serverVersionNum"])
        or int(observed["serverVersionNum"]) // 10000 != 17
    ):
        raise RestoreError(
            "restore database identity differs from the drop plan"
        )
    return observed


def repository_identity():
    root = pathlib.Path(__file__).resolve().parent.parent
    commit = ARCHIVE.run(
        ["git", "-C", root, "rev-parse", "HEAD"]
    ).stdout.decode().strip()
    return {
        "gitCommit": commit,
        "toolPath": str(pathlib.Path(__file__).resolve().relative_to(root)),
        "toolSha256": sha256_path(__file__),
    }


def psql_mutation(args, sql, *, timeout=300):
    completed = ARCHIVE.run(
        [
            "docker",
            "exec",
            "-e",
            "PGCONNECT_TIMEOUT=10",
            "-e",
            (
                "PGOPTIONS=-c lock_timeout=5s "
                "-c statement_timeout=180s "
                "-c idle_in_transaction_session_timeout=240s "
                "-c application_name=fst-snapshot-generation-restore"
            ),
            args.source_container,
            "psql",
            "-X",
            "-q",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            args.pg_user,
            "-d",
            args.pg_database,
            "-At",
            "-c",
            sql,
        ],
        timeout=timeout,
    )
    return completed.stdout.decode("utf-8").strip()


def inspect_restore_image(image):
    image_id = json.loads(
        ARCHIVE.run(
            ["docker", "image", "inspect", image]
        ).stdout.decode("utf-8")
    )[0]["Id"]
    version = ARCHIVE.run(
        [
            "docker",
            "run",
            "--rm",
            "--network",
            "none",
            image_id,
            "pg_restore",
            "--version",
        ],
        timeout=120,
    ).stdout.decode("utf-8").strip()
    if not re.search(r"\b17(?:\.|$)", version):
        raise RestoreError(
            f"restore image does not provide PostgreSQL 17: {version}"
        )
    return image_id, version


def build_plan(args):
    drop_plan_path = validate_path(args.drop_plan)
    drop_report_path = validate_path(args.drop_report)
    output = validate_path(args.output, exists=False)
    restore_list = validate_path(args.restore_list, exists=False)
    drop_plan = validate_drop_plan(
        drop_plan_path,
        args.expected_drop_plan_digest,
        args.expected_drop_operation_id,
    )
    validate_drop_report(drop_report_path, drop_plan)
    bundle, _ = validate_bundle(
        drop_plan["recoveryBundlePath"],
        drop_plan["recoveryBundleManifestSha256"],
    )
    if any(
        candidate == bundle or bundle in candidate.parents
        for candidate in (output, restore_list)
    ):
        raise RestoreError(
            "restore plan outputs cannot be written inside the sealed recovery bundle"
        )
    authorization = resolve_restore_tool_authorization(
        args,
        drop_plan,
        bundle,
        authorization_id=getattr(
            args,
            "authorization_id",
            None),
        repair_package=getattr(
            args,
            "repair_package",
            None),
        repair_package_manifest_sha256=
            getattr(
                args,
                "expected_repair_package_manifest_sha256",
                None),
    )
    archive_helper = pathlib.Path(__file__).with_name(
        "postgres-snapshot-generation-archive.py"
    )
    package = bundle / "archive"
    manifest, catalog, checksums = load_pinned_archive(package)
    proof_path, proof = validate_fresh_proof(
        package,
        drop_plan,
        manifest,
        checksums,
    )
    if (
        checksums["manifest.json"]
        != drop_plan["activePlan"]["archive"]["packageManifestSha256"]
        or manifest["archive"]["sha256"]
        != drop_plan["activePlan"]["archive"]["archiveSha256"]
    ):
        raise RestoreError("pinned archive differs from the drop plan")
    child = child_catalog(manifest, catalog)
    selected = select_restore_toc(
        (package / "archive.toc").read_text(encoding="utf-8"),
        child,
    )
    executed = selected[:2]
    restore_list_bytes = (
        "\n".join(executed) + "\n"
    ).encode("utf-8")
    required = max(
        2 * int(child["totalBytes"])
        + int(manifest["archive"]["bytes"])
        + 1024**3,
        2 * 1024**3,
    )
    reserve = int(drop_plan["capacityReserveBytes"])
    source_identity = ARCHIVE.container_identity(
        args.source_container,
        pathlib.Path("/mnt/docker-storage"),
    )
    pgdata_path = pathlib.Path(
        source_identity["pgdataMount"]["source"]
    ).resolve(strict=True)
    if pgdata_path.stat().st_dev != bundle.stat().st_dev:
        raise RestoreError(
            "restore archive and source PGDATA are not on the same FST device"
        )
    capacity_measured_at = utc_now()
    available_capacity = shutil.disk_usage(pgdata_path).free
    if available_capacity < required + reserve:
        raise RestoreError(
            "insufficient current capacity for exact logical restore plus reserve"
        )
    image_id, image_version = inspect_restore_image(
        args.postgres_image)
    if image_id.removeprefix("sha256:") != drop_plan[
        "restoreImageIdSha256"
    ]:
        raise RestoreError(
            "restore image differs from the sealed drop plan"
        )
    state = database_drop_state(args, drop_plan)
    database_identity = require_database_identity(
        args,
        drop_plan,
    )
    if (
        state.get("dropExists") is not True
        or state.get("restoreExists") is True
        or state.get("originalExists") is True
        or state.get("oldOidExists") is True
        or state.get("holdActive") is not True
    ):
        raise RestoreError("database is not in exact dropped state")
    plan = {
        "schemaVersion": SCHEMA_VERSION,
        "toolId": TOOL_ID,
        "generatedAtUtc": utc_now(),
        "dropPlanPath": str(drop_plan_path),
        "dropPlanSha256": sha256_path(drop_plan_path),
        "dropReportPath": str(drop_report_path),
        "dropReportSha256": sha256_path(drop_report_path),
        "dropPlanDigest": drop_plan["planDigest"],
        "dropOperationId": drop_plan["dropOperationId"],
        "recoveryBundlePath": str(bundle),
        "recoveryBundleManifestSha256":
            drop_plan["recoveryBundleManifestSha256"],
        "archiveManifestSha256": checksums["manifest.json"],
        "archiveSha256": manifest["archive"]["sha256"],
        "freshProofManifestPath": str(proof_path),
        "freshProofManifestSha256":
            drop_plan["freshProofManifestSha256"],
        "freshProofCompletedAtUtc":
            proof["completedAtUtc"],
        "restoreListPath": str(restore_list),
        "restoreListSha256": hashlib.sha256(
            restore_list_bytes
        ).hexdigest(),
        "selectedTocEntries": selected,
        "executedTocEntries": executed,
        "archivedIndexNames": {
            "pk": next(
                index["indexName"]
                for index in child["indexes"]
                if index.get("isPrimary")
            ),
            "score": next(
                index["indexName"]
                for index in child["indexes"]
                if not index.get("isPrimary")
            ),
        },
        "semanticCatalogSha256":
            drop_plan["activeSemantic"][
                "semanticCatalogSha256"],
        "logicalIndexShapeSha256":
            drop_plan["activeSemantic"][
                "logicalIndexShapeSha256"],
        "target": drop_plan["activePlan"]["archive"],
        "requiredCapacityBytes": required,
        "capacityReserveBytes": reserve,
        "availableCapacityBytes": available_capacity,
        "capacityMeasuredAtUtc": capacity_measured_at,
        "capacityFilesystemPath": str(pgdata_path),
        "capacityDeviceId": int(pgdata_path.stat().st_dev),
        "repository": repository_identity(),
        "archiveHelperSha256": sha256_path(archive_helper),
        "databaseIdentity": database_identity,
        "restoreImageId": image_id,
        "restoreImageVersion": image_version,
        "restoreToolAuthorization": authorization,
        "explicitApprovalRequired": True,
    }
    digest = stable_hash(plan)
    plan["planDigest"] = digest
    plan["restoreOperationId"] = stable_hash(
        {"toolId": TOOL_ID, "planDigest": digest}
    )[:32]
    try:
        write_new_bytes(restore_list, restore_list_bytes)
        write_new_json(output, plan)
    except Exception:
        with contextlib.suppress(FileNotFoundError):
            restore_list.unlink()
        with contextlib.suppress(FileNotFoundError):
            output.unlink()
        raise
    return plan


def validate_restore_plan(
    path,
    expected_digest,
    expected_operation,
    args,
):
    plan = load_json(path)
    digest = plan.get("planDigest")
    operation = plan.get("restoreOperationId")
    unsigned = dict(plan)
    unsigned.pop("planDigest", None)
    unsigned.pop("restoreOperationId", None)
    if (
        plan.get("schemaVersion") != SCHEMA_VERSION
        or plan.get("toolId") != TOOL_ID
        or plan.get("explicitApprovalRequired") is not True
        or not SHA256.fullmatch(str(digest or ""))
        or stable_hash(unsigned) != digest
        or stable_hash({"toolId": TOOL_ID, "planDigest": digest})[:32]
        != operation
        or digest != expected_digest
        or operation != expected_operation
        or plan.get("repository", {}).get("toolSha256")
        != sha256_path(__file__)
        or plan.get("repository", {}).get("gitCommit")
        != repository_identity()["gitCommit"]
        or plan.get("archiveHelperSha256")
        != sha256_path(
            pathlib.Path(__file__).with_name(
                "postgres-snapshot-generation-archive.py")
        )
        or int(plan.get("requiredCapacityBytes", 0)) <= 0
        or int(plan.get("capacityReserveBytes", -1)) < 0
        or int(plan.get("availableCapacityBytes", 0))
        < int(plan.get("requiredCapacityBytes", 0))
        + int(plan.get("capacityReserveBytes", 0))
        or not plan.get("capacityMeasuredAtUtc")
        or not str(plan.get("capacityFilesystemPath", "")).startswith(
            "/mnt/docker-storage/"
        )
        or int(plan.get("capacityDeviceId", -1)) < 0
        or len(plan.get("selectedTocEntries") or []) != 4
        or len(plan.get("executedTocEntries") or []) != 2
        or (plan.get("selectedTocEntries") or [])[:2]
        != plan.get("executedTocEntries")
        or set(
            (plan.get("archivedIndexNames") or {}).keys()
        )
        != {"pk", "score"}
        or not SHA256.fullmatch(
            str(plan.get("semanticCatalogSha256", ""))
        )
        or not SHA256.fullmatch(
            str(plan.get("logicalIndexShapeSha256", ""))
        )
    ):
        raise RestoreError("restore plan identity is invalid")
    drop_plan_path = validate_path(plan["dropPlanPath"])
    if sha256_path(drop_plan_path) != plan["dropPlanSha256"]:
        raise RestoreError("drop plan file changed")
    drop_plan = validate_drop_plan(
        drop_plan_path,
        plan["dropPlanDigest"],
        plan["dropOperationId"],
    )
    drop_report_path = validate_path(plan["dropReportPath"])
    if (
        sha256_path(drop_report_path)
        != plan["dropReportSha256"]
    ):
        raise RestoreError("drop report file changed")
    validate_path(plan["recoveryBundlePath"], directory=True)
    restore_list = validate_path(plan["restoreListPath"])
    if sha256_path(restore_list) != plan["restoreListSha256"]:
        raise RestoreError("restore TOC list changed")
    bundle, _ = validate_bundle(
        plan["recoveryBundlePath"],
        plan["recoveryBundleManifestSha256"],
    )
    authorization_plan = plan.get(
        "restoreToolAuthorization")
    if not isinstance(authorization_plan, dict):
        raise RestoreError(
            "restore plan lacks tool authorization evidence"
        )
    authorization = resolve_restore_tool_authorization(
        args,
        drop_plan,
        bundle,
        authorization_id=
            authorization_plan.get("authorizationId"),
        repair_package=
            authorization_plan.get("repairPackagePath"),
        repair_package_manifest_sha256=
            authorization_plan.get(
                "repairPackageManifestSha256"),
    )
    if authorization != authorization_plan:
        raise RestoreError(
            "restore plan tool authorization changed"
        )
    package = pathlib.Path(
        plan["recoveryBundlePath"]
    ) / "archive"
    manifest, _, checksums = load_pinned_archive(package)
    if (
        drop_plan["activeSemantic"][
            "semanticCatalogSha256"
        ]
        != plan["semanticCatalogSha256"]
        or drop_plan["activeSemantic"][
            "logicalIndexShapeSha256"
        ]
        != plan["logicalIndexShapeSha256"]
    ):
        raise RestoreError(
            "restore semantic evidence differs from the drop plan"
        )
    validate_drop_report(drop_report_path, drop_plan)
    validate_fresh_proof(
        package,
        drop_plan,
        manifest,
        checksums,
    )
    return plan


def restore_list_text(plan):
    entries = pathlib.Path(
        plan["restoreListPath"]
    ).read_text(encoding="utf-8").splitlines()
    if len(entries) != 2:
        raise RestoreError("restore TOC list is incomplete")
    return "\n".join(entries) + "\n"


def revalidate_restore_tool_authorization(
    args,
    plan,
):
    drop_plan_path = validate_path(
        plan["dropPlanPath"])
    drop_plan = validate_drop_plan(
        drop_plan_path,
        plan["dropPlanDigest"],
        plan["dropOperationId"],
    )
    bundle, _ = validate_bundle(
        plan["recoveryBundlePath"],
        plan["recoveryBundleManifestSha256"],
    )
    authorization_plan = plan.get(
        "restoreToolAuthorization")
    if not isinstance(authorization_plan, dict):
        raise RestoreError(
            "restore plan lacks tool authorization evidence"
        )
    observed = resolve_restore_tool_authorization(
        args,
        drop_plan,
        bundle,
        authorization_id=
            authorization_plan.get("authorizationId"),
        repair_package=
            authorization_plan.get("repairPackagePath"),
        repair_package_manifest_sha256=
            authorization_plan.get(
                "repairPackageManifestSha256"),
    )
    if observed != authorization_plan:
        raise RestoreError(
            "restore-tool authorization changed"
        )
    return observed


def run_restore_client(args, plan):
    revalidate_restore_tool_authorization(
        args,
        plan,
    )
    pgpass = pathlib.Path(
        os.environ.get("FST_SNAPSHOT_RESTORE_PGPASSFILE", "")
    )
    if (
        not pgpass.is_file()
        or pgpass.is_symlink()
        or pgpass.stat().st_mode & 0o077
    ):
        raise RestoreError(
            "FST_SNAPSHOT_RESTORE_PGPASSFILE must name a mode-0600 regular file"
        )
    source = ARCHIVE.container_identity(
        args.source_container,
        pathlib.Path("/mnt/docker-storage"),
    )
    image = json.loads(
        ARCHIVE.run(
            ["docker", "image", "inspect", args.postgres_image]
        ).stdout.decode("utf-8")
    )[0]["Id"]
    if image != plan["restoreImageId"]:
        raise RestoreError("restore image identity changed")
    bundle = pathlib.Path(plan["recoveryBundlePath"])
    package = bundle / "archive"
    list_path = pathlib.Path(plan["restoreListPath"])
    name = (
        "fst-snapshot-restore-"
        + plan["restoreOperationId"][:20]
    )
    stale = ARCHIVE.run(
        ["docker", "inspect", name],
        check=False,
    )
    if stale.returncode == 0:
        raise RestoreError(
            "restore client identity already owns a container"
        )
    command = [
        "docker",
        "run",
        "--rm",
        "--name",
        name,
        "--label",
        f"fst.tool={TOOL_ID}",
        "--label",
        f"fst.restore={plan['restoreOperationId']}",
        "--network",
        f"container:{source['containerId']}",
        "--read-only",
        "--user",
        f"{os.getuid()}:{os.getgid()}",
        "--pids-limit",
        "128",
        "--cpus",
        "1",
        "--memory",
        "1g",
        "-e",
        "PGPASSFILE=/run/secrets/pgpass",
        "--mount",
        ARCHIVE.docker_bind_mount(
            package,
            "/archive",
            readonly=True,
        ),
        "--mount",
        ARCHIVE.docker_bind_mount(
            list_path.parent,
            "/restore-plan",
            readonly=True,
        ),
        "--mount",
        ARCHIVE.docker_bind_mount(
            pgpass,
            "/run/secrets/pgpass",
            readonly=True,
        ),
        image,
        "pg_restore",
        "--exit-on-error",
        "--single-transaction",
        "--no-owner",
        "--no-privileges",
        "--use-list",
        f"/restore-plan/{list_path.name}",
        "--host",
        "127.0.0.1",
        "--username",
        args.pg_user,
        "--dbname",
        args.pg_database,
        "/archive/archive.custom",
    ]
    operation_error = None
    try:
        ARCHIVE.run(command, timeout=args.timeout_seconds)
    except Exception as error:
        operation_error = error
    finally:
        inspected = ARCHIVE.run(
            ["docker", "inspect", name],
            check=False,
        )
        if inspected.returncode == 0:
            details = json.loads(inspected.stdout)[0]
            labels = details.get("Config", {}).get("Labels") or {}
            if (
                labels.get("fst.tool") != TOOL_ID
                or labels.get("fst.restore")
                != plan["restoreOperationId"]
            ):
                raise RestoreError(
                    "refusing to remove an unowned restore client"
                )
            ARCHIVE.run(
                ["docker", "rm", "-f", "-v", name],
                timeout=120,
            )
        if ARCHIVE.run(
            ["docker", "inspect", name],
            check=False,
        ).returncode == 0:
            raise RestoreError(
                "restore client container absence was not proven"
            )
    if operation_error is not None:
        raise operation_error


def execute_restore(args, plan):
    drop_plan = load_json(plan["dropPlanPath"])
    drop_report = load_json(plan["dropReportPath"])
    validate_actor(args.restored_by, "restored-by")
    validate_actor(args.restore_reference, "restore-reference")
    prior_references = {
        drop_report.get("reference"),
        (drop_report.get("evidence") or {}).get(
            "approvalReference"
        ),
    }
    if args.restore_reference in prior_references:
        raise RestoreError(
            "restore approval reference must differ from DROP approval"
        )
    require_database_identity(args, drop_plan)
    state = database_drop_state(args, drop_plan)
    if state.get("restoreExists") is True:
        confirmed = confirm_restore(args, plan)
        return {
            "schemaVersion": SCHEMA_VERSION,
            "toolId": TOOL_ID,
            "action": "restore",
            "status": "restored",
            "commitOutcome": "already-committed",
            "restoreOperationId": plan["restoreOperationId"],
            "dropOperationId": plan["dropOperationId"],
            "planDigest": plan["planDigest"],
            "completedAtUtc": utc_now(),
            "actor": args.restored_by,
            "reference": args.restore_reference,
            "databaseEvidence": confirmed,
        }
    if (
        state.get("dropExists") is not True
        or state.get("oldOidExists") is True
        or state.get("holdActive") is not True
    ):
        raise RestoreError("database is not in exact dropped state")
    source_identity = ARCHIVE.container_identity(
        args.source_container,
        pathlib.Path("/mnt/docker-storage"),
    )
    pgdata_path = pathlib.Path(
        source_identity["pgdataMount"]["source"]
    ).resolve(strict=True)
    if (
        str(pgdata_path) != plan["capacityFilesystemPath"]
        or int(pgdata_path.stat().st_dev)
        != int(plan["capacityDeviceId"])
        or pathlib.Path(
            plan["recoveryBundlePath"]
        ).stat().st_dev
        != pgdata_path.stat().st_dev
    ):
        raise RestoreError(
            "restore capacity filesystem identity changed"
        )
    if shutil.disk_usage(pgdata_path).free < (
        int(plan["requiredCapacityBytes"])
        + int(plan["capacityReserveBytes"])
    ):
        raise RestoreError(
            "current restore capacity is below the sealed requirement and reserve"
        )
    resumed_staging = state.get("originalExists") is True
    if not resumed_staging:
        run_restore_client(args, plan)
    target = plan["target"]
    check_name = (
        f"ck_sgr_{int(target['snapshotId'])}_"
        f"{plan['restoreOperationId'][:12]}"
    )
    trigger_name = (
        f"trg_sgr_{int(target['snapshotId'])}_"
        f"{plan['restoreOperationId'][:12]}"
    )
    relation = target["childRelation"]
    setup_sql = f"""
        BEGIN;
        SET LOCAL lock_timeout = '5s';
        SET LOCAL statement_timeout = '180s';
        DO $restore_guard$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'public.{relation}'::regclass
                  AND conname =
                        {ARCHIVE.sql_literal(check_name)})
            THEN
                ALTER TABLE public.{ARCHIVE.quote_identifier(relation)}
                    ADD CONSTRAINT {ARCHIVE.quote_identifier(check_name)}
                    CHECK (
                        snapshot_id = {int(target["snapshotId"])}
                        AND instrument =
                            {ARCHIVE.sql_literal(target["instrument"])});
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'public.{relation}'::regclass
                  AND tgname =
                        {ARCHIVE.sql_literal(trigger_name)}
                  AND NOT tgisinternal)
            THEN
                CREATE TRIGGER {ARCHIVE.quote_identifier(trigger_name)}
                    BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                    ON public.{ARCHIVE.quote_identifier(relation)}
                    FOR EACH STATEMENT EXECUTE FUNCTION
                        fst_reject_snapshot_generation_quarantine_relation_mutation();
            END IF;
        END
        $restore_guard$;
        COMMIT;
    """
    psql_mutation(
        args,
        setup_sql,
    )
    restored_catalog = ARCHIVE.capture_catalog(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        (relation,),
    )
    restored = restored_catalog[0]
    source_manifest, source_catalog, _ = load_pinned_archive(
        pathlib.Path(plan["recoveryBundlePath"])
        / "archive"
    )
    source_child = child_catalog(
        source_manifest,
        source_catalog,
    )
    if (
        restored.get("relationKind") != "r"
        or restored.get("parentOid") is not None
        or restored.get("partitionBound") not in ("", None)
        or detached_base_relation(
            restored,
            check_name,
        )
        != detached_base_relation(source_child)
    ):
        raise RestoreError(
            "restored detached child catalog differs from the archive"
        )
    fingerprint = ARCHIVE.stream_fingerprint(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        relation,
        timeout_seconds=args.timeout_seconds,
    )
    if (
        fingerprint["rowCount"] != int(target["rowCount"])
        or fingerprint["sha256"] != target["rowFingerprintSha256"]
        or int(restored["oid"]) == int(target["childOid"])
    ):
        raise RestoreError("restored staging data or physical identity is invalid")
    evidence = {
        "restoredCatalog": restored_catalog,
        "rowFingerprint": fingerprint,
        "temporaryCheck": check_name,
        "mutationGuard": trigger_name,
    }
    authorization = revalidate_restore_tool_authorization(
        args,
        plan,
    )
    sql = f"""
        SELECT fst_restore_snapshot_generation(
            {ARCHIVE.sql_literal(plan["restoreOperationId"])},
            {ARCHIVE.sql_literal(plan["planDigest"])},
            {ARCHIVE.sql_literal(plan["dropOperationId"])},
            {int(restored["oid"])},
            {int(restored["relfilenode"])},
            {int(fingerprint["rowCount"])},
            {ARCHIVE.sql_literal(fingerprint["sha256"])},
            {ARCHIVE.sql_literal(plan["target"]["logicalCatalogSha256"])},
            {ARCHIVE.sql_literal(plan["semanticCatalogSha256"])},
            {ARCHIVE.sql_literal(plan["logicalIndexShapeSha256"])},
            {sql_nullable_literal(authorization["authorizationId"])},
            {ARCHIVE.sql_literal(authorization["executingToolSha256"])},
            {sql_nullable_literal(authorization["validatorBaseToolSha256"])},
            {sql_nullable_literal(authorization["authorizedArchiveHelperSha256"])},
            {sql_nullable_literal(authorization["repairPackageManifestSha256"])},
            {ARCHIVE.sql_literal(json.dumps(plan["archivedIndexNames"], sort_keys=True))}::jsonb,
            {ARCHIVE.sql_literal(check_name)},
            {ARCHIVE.sql_literal(trigger_name)},
            {ARCHIVE.sql_literal(args.restored_by)},
            {ARCHIVE.sql_literal(args.restore_reference)},
            {ARCHIVE.sql_literal(json.dumps(evidence, sort_keys=True))}::jsonb)
    """
    commit_outcome = "committed"
    try:
        result = psql_mutation(args, sql).strip()
        if plan["restoreOperationId"] not in result:
            raise RestoreError("restore function returned another operation")
    except Exception:
        with contextlib.suppress(Exception):
            reconciled = confirm_restore(args, plan)
            if reconciled.get("restoreExists") is True:
                commit_outcome = "reconciled-committed"
            else:
                raise
        if commit_outcome != "reconciled-committed":
            raise
    final_catalog = ARCHIVE.capture_catalog(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        (
            "leaderboard_entries_snapshot",
            target["rootRelation"],
            target["childRelation"],
        ),
    )
    final_fingerprint = ARCHIVE.stream_fingerprint(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        target["childRelation"],
        timeout_seconds=args.timeout_seconds,
    )
    final_logical_hash = ARCHIVE.stable_hash(
        ARCHIVE.logical_catalog(final_catalog)
    )
    final_by_name = {
        relation["name"]: relation
        for relation in final_catalog
    }
    final_child = final_by_name[
        target["childRelation"]
    ]
    final_root = final_by_name[
        target["rootRelation"]
    ]
    final_top = final_by_name[
        "leaderboard_entries_snapshot"
    ]
    final_shape_hash = logical_index_shape_sha256(
        final_child,
        final_root,
        final_top,
    )
    if (
        final_fingerprint != fingerprint
        or detached_base_relation(final_child)
        != detached_base_relation(source_child)
        or final_shape_hash
        != plan["logicalIndexShapeSha256"]
    ):
        raise RestoreError(
            "attached restore data or semantic catalog changed"
        )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "toolId": TOOL_ID,
        "action": "restore",
        "status": "restored",
        "commitOutcome": commit_outcome,
        "restoreOperationId": plan["restoreOperationId"],
        "dropOperationId": plan["dropOperationId"],
        "planDigest": plan["planDigest"],
        "completedAtUtc": utc_now(),
        "actor": args.restored_by,
        "reference": args.restore_reference,
        "resumedStaging": resumed_staging,
        "newChildOid": int(restored["oid"]),
        "newChildRelfilenode": int(restored["relfilenode"]),
        "rowFingerprint": final_fingerprint,
        "logicalCatalogSha256": final_logical_hash,
        "semanticCatalogSha256":
            plan["semanticCatalogSha256"],
        "logicalIndexShapeSha256":
            final_shape_hash,
        "catalog": final_catalog,
    }


def confirm_restore(args, plan):
    require_database_identity(
        args,
        load_json(plan["dropPlanPath"]),
    )
    sql = f"""
        SELECT json_build_object(
            'restoreExists', restore_row.restore_operation_id IS NOT NULL,
            'restoreOperationId', restore_row.restore_operation_id,
            'newOid', restore_row.restored_child_oid,
            'newRelfilenode', restore_row.restored_child_relfilenode,
            'pinnedToolSha256',
                restore_row.pinned_tool_sha256,
            'executingToolSha256',
                restore_row.executing_tool_sha256,
            'authorizationId',
                restore_row.authorization_id,
            'originalRelationExists',
                to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        drop_row.child_relation)) IS NOT NULL,
            'originalIdentityMatches', EXISTS (
                SELECT 1
                FROM pg_class child
                JOIN pg_namespace namespace
                  ON namespace.oid = child.relnamespace
                WHERE child.oid =
                        restore_row.restored_child_oid
                  AND child.relfilenode::BIGINT =
                        restore_row.restored_child_relfilenode
                  AND namespace.nspname =
                        restore_row.child_schema
                  AND child.relname =
                        restore_row.child_relation),
            'attached', EXISTS (
                SELECT 1
                FROM pg_inherits inheritance
                WHERE inheritance.inhrelid =
                        restore_row.restored_child_oid
                  AND inheritance.inhparent =
                        restore_row.root_oid),
            'holdActive', EXISTS (
                SELECT 1
                FROM snapshot_generation_retention_holds hold_row
                WHERE hold_row.hold_id = restore_row.hold_id
                  AND hold_row.released_at IS NULL),
            'mutationGuardPresent', EXISTS (
                SELECT 1
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        restore_row.restored_child_oid
                  AND trigger_row.tgname =
                        'trg_sgr_' ||
                        restore_row.snapshot_id::TEXT ||
                        '_' ||
                        left(
                            restore_row.restore_operation_id,
                            12)
                  AND NOT trigger_row.tgisinternal
                  AND trigger_row.tgenabled = 'O'
                  AND trigger_row.tgfoid =
                        'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                            ::regprocedure),
            'finalized', EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_finalizations
                    finalization
                WHERE finalization.restore_operation_id =
                        restore_row.restore_operation_id))
        FROM snapshot_generation_drop_operations drop_row
        LEFT JOIN snapshot_generation_restore_operations
            restore_row
          ON restore_row.drop_operation_id =
                drop_row.drop_operation_id
         AND restore_row.restore_operation_id =
                {ARCHIVE.sql_literal(plan["restoreOperationId"])}
         AND restore_row.plan_digest =
                {ARCHIVE.sql_literal(plan["planDigest"])}
        WHERE drop_row.drop_operation_id =
            {ARCHIVE.sql_literal(plan["dropOperationId"])}
    """
    state = ARCHIVE.psql_json(
        args.source_container,
        args.pg_user,
        args.pg_database,
        sql,
    )
    if not isinstance(state, dict):
        raise RestoreError("drop operation is unavailable")
    authorization = plan["restoreToolAuthorization"]
    if (
        state.get("restoreExists") is not True
        or state.get("attached") is not True
        or state.get("originalRelationExists") is not True
        or state.get("originalIdentityMatches") is not True
        or state.get("pinnedToolSha256")
            != authorization["pinnedToolSha256"]
        or state.get("executingToolSha256")
            != authorization["executingToolSha256"]
        or state.get("authorizationId")
            != authorization["authorizationId"]
        or (
            state.get("finalized") is True
            and (
                state.get("holdActive") is not False
                or state.get("mutationGuardPresent")
                is not False
            )
        )
        or (
            state.get("finalized") is not True
            and (
                state.get("holdActive") is not True
                or state.get("mutationGuardPresent")
                is not True
            )
        )
    ):
        raise RestoreError("restore commit state is incomplete")
    return state


def attest_restore(args, plan):
    validate_actor(args.attested_by, "attested-by")
    parity = validate_route_pair(
        validate_path(args.baseline_route_manifest),
        validate_path(args.candidate_route_manifest),
    )
    state = confirm_restore(args, plan)
    target = plan["target"]
    catalog = ARCHIVE.capture_catalog(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        (
            "leaderboard_entries_snapshot",
            target["rootRelation"],
            target["childRelation"],
        ),
    )
    fingerprint = ARCHIVE.stream_fingerprint(
        args.source_container,
        args.pg_user,
        args.pg_database,
        "public",
        target["childRelation"],
        timeout_seconds=args.timeout_seconds,
    )
    logical_catalog_sha256 = ARCHIVE.stable_hash(
        ARCHIVE.logical_catalog(catalog)
    )
    package = (
        pathlib.Path(plan["recoveryBundlePath"])
        / "archive"
    )
    source_manifest, source_catalog, _ = (
        load_pinned_archive(package)
    )
    source_child = child_catalog(
        source_manifest,
        source_catalog,
    )
    final_child = next(
        (
            relation
            for relation in catalog
            if relation.get("name")
            == target["childRelation"]
        ),
        None,
    )
    final_root = next(
        (
            relation
            for relation in catalog
            if relation.get("name")
            == target["rootRelation"]
        ),
        None,
    )
    final_top = next(
        (
            relation
            for relation in catalog
            if relation.get("name")
            == "leaderboard_entries_snapshot"
        ),
        None,
    )
    if (
        final_child is None
        or final_root is None
        or final_top is None
    ):
        raise RestoreError(
            "restored attestation catalog is incomplete"
        )
    final_index_shape_sha256 = (
        logical_index_shape_sha256(
            final_child,
            final_root,
            final_top,
        )
    )
    if (
        fingerprint["rowCount"] != int(target["rowCount"])
        or fingerprint["sha256"]
        != target["rowFingerprintSha256"]
        or detached_base_relation(final_child)
        != detached_base_relation(source_child)
        or final_index_shape_sha256
        != plan["logicalIndexShapeSha256"]
        or int(state["newOid"]) == int(target["childOid"])
    ):
        raise RestoreError(
            "restored attestation data or semantic topology changed"
        )
    state = {
        **state,
        "rowFingerprint": fingerprint,
        "logicalCatalogSha256": logical_catalog_sha256,
        "archivedLogicalCatalogSha256":
            target["logicalCatalogSha256"],
        "semanticCatalogSha256":
            plan["semanticCatalogSha256"],
        "logicalIndexShapeSha256":
            final_index_shape_sha256,
        "baseRelationSemanticMatch": True,
        "catalog": catalog,
    }
    evidence_hash = stable_hash(
        {"parity": parity, "database": state}
    )
    sql = f"""
        SELECT fst_record_snapshot_generation_restore_attestation(
            {ARCHIVE.sql_literal(plan["restoreOperationId"])},
            {int(parity["publicationId"])},
            {int(parity["publishedScrapeId"])},
            55,
            {ARCHIVE.sql_literal(parity["baselineManifestSha256"])},
            {ARCHIVE.sql_literal(parity["candidateManifestSha256"])},
            {ARCHIVE.sql_literal(json.dumps(state, sort_keys=True))}::jsonb,
            {ARCHIVE.sql_literal(evidence_hash)},
            {ARCHIVE.sql_literal(args.attested_by)})
    """
    psql_mutation(args, sql)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "toolId": TOOL_ID,
        "action": "attest",
        "status": "accepted",
        "restoreOperationId": plan["restoreOperationId"],
        "completedAtUtc": utc_now(),
        "actor": args.attested_by,
        "parity": parity,
        "databaseEvidence": state,
        "evidenceSha256": evidence_hash,
    }


def finalize_restore(args, plan):
    validate_actor(args.finalized_by, "finalized-by")
    validate_actor(
        args.finalize_reference,
        "finalize-reference",
    )
    state = confirm_restore(args, plan)
    evidence = {
        "confirmedRestore": state,
        "finalizedAtUtc": utc_now(),
    }
    sql = f"""
        SELECT fst_finalize_snapshot_generation_restore(
            {ARCHIVE.sql_literal(plan["restoreOperationId"])},
            {ARCHIVE.sql_literal(args.finalized_by)},
            {ARCHIVE.sql_literal(args.finalize_reference)},
            {ARCHIVE.sql_literal(json.dumps(evidence, sort_keys=True))}::jsonb)
    """
    psql_mutation(args, sql)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "toolId": TOOL_ID,
        "action": "finalize",
        "status": "finalized",
        "restoreOperationId": plan["restoreOperationId"],
        "completedAtUtc": utc_now(),
        "actor": args.finalized_by,
        "reference": args.finalize_reference,
    }


def build_parser():
    parser = argparse.ArgumentParser(
        description="Exact snapshot-generation logical restore"
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    plan = subparsers.add_parser("plan")
    plan.add_argument("--drop-plan", required=True)
    plan.add_argument("--drop-report", required=True)
    plan.add_argument("--expected-drop-plan-digest", required=True)
    plan.add_argument("--expected-drop-operation-id", required=True)
    plan.add_argument("--restore-list", required=True)
    plan.add_argument("--output", required=True)
    plan.add_argument("--source-container", default="fst-postgres")
    plan.add_argument("--pg-user", default="fst")
    plan.add_argument("--pg-database", default="fstservice")
    plan.add_argument("--postgres-image", required=True)
    plan.add_argument("--authorization-id")
    plan.add_argument("--repair-package")
    plan.add_argument(
        "--expected-repair-package-manifest-sha256")

    for command_name in ("restore", "confirm"):
        command = subparsers.add_parser(command_name)
        command.add_argument("--plan", required=True)
        command.add_argument("--expected-plan-digest", required=True)
        command.add_argument("--expected-operation-id", required=True)
        command.add_argument("--output", required=True)
        command.add_argument("--source-container", default="fst-postgres")
        command.add_argument("--pg-user", default="fst")
        command.add_argument("--pg-database", default="fstservice")
        command.add_argument("--postgres-image")
        command.add_argument("--timeout-seconds", type=int, default=7200)
    restore = subparsers.choices["restore"]
    restore.add_argument("--execute", action="store_true")
    restore.add_argument("--restored-by", required=True)
    restore.add_argument("--restore-reference", required=True)

    attest = subparsers.add_parser("attest")
    attest.add_argument("--plan", required=True)
    attest.add_argument("--expected-plan-digest", required=True)
    attest.add_argument("--expected-operation-id", required=True)
    attest.add_argument("--baseline-route-manifest", required=True)
    attest.add_argument("--candidate-route-manifest", required=True)
    attest.add_argument("--attested-by", required=True)
    attest.add_argument("--output", required=True)
    attest.add_argument("--source-container", default="fst-postgres")
    attest.add_argument("--pg-user", default="fst")
    attest.add_argument("--pg-database", default="fstservice")
    attest.add_argument(
        "--timeout-seconds",
        type=int,
        default=7200,
    )

    finalize = subparsers.add_parser("finalize")
    finalize.add_argument("--plan", required=True)
    finalize.add_argument("--expected-plan-digest", required=True)
    finalize.add_argument("--expected-operation-id", required=True)
    finalize.add_argument("--finalized-by", required=True)
    finalize.add_argument("--finalize-reference", required=True)
    finalize.add_argument("--output", required=True)
    finalize.add_argument("--source-container", default="fst-postgres")
    finalize.add_argument("--pg-user", default="fst")
    finalize.add_argument("--pg-database", default="fstservice")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        if (
            hasattr(args, "timeout_seconds")
            and args.timeout_seconds <= 0
        ):
            raise RestoreError(
                "--timeout-seconds must be positive"
            )
        if args.command == "plan":
            result = build_plan(args)
        else:
            plan_path = validate_path(args.plan)
            output = validate_path(args.output, exists=False)
            plan = validate_restore_plan(
                plan_path,
                args.expected_plan_digest,
                args.expected_operation_id,
                args,
            )
            if args.command == "restore":
                if not args.execute:
                    raise RestoreError("restore requires --execute")
                if not args.postgres_image:
                    raise RestoreError("restore requires --postgres-image")
                result = execute_restore(args, plan)
            elif args.command == "confirm":
                result = {
                    "schemaVersion": SCHEMA_VERSION,
                    "toolId": TOOL_ID,
                    "action": "confirm",
                    "status": "restored",
                    "restoreOperationId": plan["restoreOperationId"],
                    "completedAtUtc": utc_now(),
                    "databaseEvidence": confirm_restore(args, plan),
                }
            elif args.command == "attest":
                result = attest_restore(args, plan)
            else:
                result = finalize_restore(args, plan)
            result = seal_report(result)
            write_new_json(output, result)
        print(json.dumps(result, sort_keys=True))
        return 0
    except (
        RestoreError,
        ARCHIVE.ArchiveError,
        OSError,
        subprocess.SubprocessError,
        ValueError,
        TypeError,
        KeyError,
    ) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
