#!/usr/bin/env python3
"""Secret-free tests of the owned authentication guard; no password artifacts are created."""
import base64
import hashlib
import hmac
import io
import json
import os
import unittest
from unittest.mock import patch

from owned_postgres_auth import SecretGuard, scram_verifier, validation_environment


class MemoryFile:
    def __init__(self, value):
        self._value = value

    def is_symlink(self):
        return False

    def is_file(self):
        return True

    def open(self, _mode):
        return io.BytesIO(self._value)


class OwnedPostgresAuthTests(unittest.TestCase):
    def test_password_is_random_nonempty_and_rejected_from_output(self):
        guard = SecretGuard()
        password = guard.password()
        self.assertTrue(len(password) >= 64)
        self.assertTrue(password != guard.password())
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            guard.require_safe("output: " + password)
        self.assertTrue(password not in json.dumps(guard.evidence()))

    def test_registered_connection_is_not_permitted_in_json(self):
        guard = SecretGuard()
        connection = guard.register("Host=owned;Username=fixture;Password=" + guard.password())
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            guard.require_safe(json.dumps({"connection": connection}))
        self.assertTrue(connection not in str(guard))

    def test_stream_scan_detects_a_sentinel_crossing_chunk_boundary(self):
        guard = SecretGuard()
        password = guard.password()
        value = b"x" * (1024 * 1024 - 10) + password.encode() + b"tail"
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            guard.scan_file(MemoryFile(value))
        self.assertEqual(1, guard.evidence()["violations"])

    def test_clean_stream_produces_only_secret_free_counts(self):
        guard = SecretGuard()
        guard.password()
        guard.scan_file(MemoryFile(b'{"outcome":"passed"}'))
        self.assertEqual(1, guard.evidence()["filesScanned"])
        self.assertEqual(0, guard.evidence()["violations"])

    def test_unregistered_credential_shaped_output_is_also_refused(self):
        guard = SecretGuard()
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            guard.require_safe("Host=owned;Username=fixture;Password=not-a-real-secret")
        self.assertEqual(1, guard.evidence()["violations"])

    def test_unregistered_verifier_is_not_an_artifact(self):
        owner = SecretGuard()
        verifier = scram_verifier(owner.password(), owner)
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            SecretGuard().require_safe(verifier)

    def test_verifier_is_valid_scram_without_plaintext(self):
        guard = SecretGuard()
        password = guard.password()
        verifier = scram_verifier(password, guard)
        kind, work, keys = verifier.split("$")
        iterations, salt = work.split(":")
        stored, server = (base64.b64decode(key) for key in keys.split(":"))
        salted = hashlib.pbkdf2_hmac("sha256", password.encode(), base64.b64decode(salt), int(iterations))
        self.assertEqual("SCRAM-SHA-256", kind)
        self.assertTrue(hmac.compare_digest(stored, hashlib.sha256(hmac.digest(salted, b"Client Key", "sha256")).digest()))
        self.assertTrue(hmac.compare_digest(server, hmac.digest(salted, b"Server Key", "sha256")))
        self.assertTrue(password not in verifier)
        with self.assertRaisesRegex(RuntimeError, "^Credential material was suppressed"):
            guard.require_safe(verifier)

    def test_fixture_environment_does_not_copy_operator_credentials(self):
        with patch.dict(os.environ, {
            "PATH": "/usr/bin:/bin", "ConnectionStrings__PostgreSQL": "not-copied",
            "FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING": "not-copied",
            "PGPASSWORD": "not-copied", "PROVIDER_TOKEN": "not-copied",
        }, clear=True):
            self.assertEqual({"PATH": "/usr/bin:/bin"}, validation_environment())


if __name__ == "__main__":
    unittest.main()
