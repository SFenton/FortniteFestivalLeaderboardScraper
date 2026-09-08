"""Process-memory credentials and output checks for owned PostgreSQL validation only."""
import base64
import hashlib
import hmac
import os
import re
import secrets
import threading


class SecretGuard:
    def __init__(self):
        self._values = []
        self._checks = 0
        self._files = 0
        self._violations = 0

    def register(self, value):
        if not value:
            raise RuntimeError("An owned authentication sentinel must be nonempty")
        self._values.append(value.encode())
        return value

    def password(self):
        return self.register(secrets.token_hex(36))

    def require_safe(self, value):
        data = value.encode() if isinstance(value, str) else value
        self._checks += 1
        credential_shape = re.search(
            rb"(?i)(?:^|[;\s\"'])(?:password|pwd)\s*=\s*[^;\s\"']+"
            rb"|SCRAM-SHA-256\$[0-9]+:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+",
            data)
        if any(secret in data for secret in self._values) or credential_shape:
            self._violations += 1
            raise RuntimeError("Credential material was suppressed from owned validation output")
        return value

    def scan_file(self, path):
        if path.is_symlink() or not path.is_file():
            raise RuntimeError("Secret scanning requires an owned regular file")
        self._files += 1
        overlap = max((len(value) for value in self._values), default=1) - 1
        tail = b""
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                data = tail + block
                self.require_safe(data)
                tail = data[-overlap:] if overlap else b""

    def evidence(self):
        return {"runtimeSentinels": len(self._values), "checks": self._checks,
                "filesScanned": self._files, "violations": self._violations,
                "credentialsPersisted": False}


def scram_verifier(password, guard):
    salt = secrets.token_bytes(16)
    salted = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 4096)
    client = hmac.digest(salted, b"Client Key", "sha256")
    stored = hashlib.sha256(client).digest()
    server = hmac.digest(salted, b"Server Key", "sha256")
    encoded = lambda value: base64.b64encode(value).decode()
    return guard.register(f"SCRAM-SHA-256$4096:{encoded(salt)}${encoded(stored)}:{encoded(server)}")


def validation_environment():
    # Never copy an operator's connection, provider, or authentication environment into a fixture.
    return {key: os.environ[key] for key in (
        "PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "NUGET_PACKAGES",
        "SSL_CERT_FILE", "SSL_CERT_DIR",
    ) if key in os.environ}


class CapturedProcessOutput:
    """Drain both pipes in memory; validate both streams before writing either artifact."""
    def __init__(self, guard, stdout_path, stderr_path):
        self._guard = guard
        self._stdout_path = stdout_path
        self._stderr_path = stderr_path
        self._stdout = []
        self._stderr = []
        self._threads = []

    def __enter__(self):
        return self

    def attach(self, process):
        for stream, chunks in ((process.stdout, self._stdout), (process.stderr, self._stderr)):
            reader = threading.Thread(target=lambda source=stream, target=chunks: target.extend(source), daemon=True)
            reader.start()
            self._threads.append(reader)

    def stdout(self):
        return self._guard.require_safe("".join(self._stdout))

    def __exit__(self, *_):
        for reader in self._threads:
            reader.join(timeout=10)
            if reader.is_alive():
                raise RuntimeError("Owned process output capture did not terminate")
        stdout = self.stdout()
        stderr = self._guard.require_safe("".join(self._stderr))
        self._stdout_path.write_text(stdout)
        self._stderr_path.write_text(stderr)
