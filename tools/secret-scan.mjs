#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import { lstatSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const credentialNames = new Set([
  "apikey",
  "clientsecret",
  "password",
  "privatekey",
  "presharedkey",
  "tokenencryptionkey",
  "jwtsecret",
  "midiencryptionkey",
  "smtp password".replaceAll(" ", "")
]);

export function scanContent(filePath, content) {
  const findings = [];
  const add = (rule, index, key = "") => {
    findings.push({
      file: filePath,
      line: lineNumber(content, index),
      rule,
      key
    });
  };

  if (filePath.endsWith(".json")) {
    try {
      scanJson(JSON.parse(content), "", (key, value) => {
        if (isCredentialName(key) && typeof value === "string" && !isPlaceholder(value, filePath)) {
          const keyIndex = content.indexOf(`"${key}"`);
          add("credential-json-default", Math.max(0, keyIndex), key);
        }
      });
    } catch {
      // Non-JSON rules below still inspect malformed or JSON-like fixtures.
    }
  }

  const csharpAssignment =
    /\b(?:const\s+)?string\s+([A-Za-z_][A-Za-z0-9_]*(?:Key|Secret|Password|Token|Credential)[A-Za-z0-9_]*)\s*=\s*"([^"]*)"/g;
  for (const match of content.matchAll(csharpAssignment)) {
    if (isCredentialName(match[1]) && !isPlaceholder(match[2], filePath)) {
      add("credential-code-default", match.index, match[1]);
    }
  }

  const interpolationDefault =
    /\$\{([A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|SECRET|KEY|TOKEN)[A-Za-z0-9_]*):-([^}]*)\}/g;
  for (const match of content.matchAll(interpolationDefault)) {
    if (!match[1].includes("PUBLIC_KEY") && !isPlaceholder(match[2], filePath)) {
      add("credential-interpolation-default", match.index, match[1]);
    }
  }

  if (/\.(?:ya?ml|env|example)$/i.test(filePath) || /(?:^|\/)\.env(?:\.|$)/i.test(filePath)) {
    const assignment =
      /^(?:[ \t]*[-]?[ \t]*)?([A-Za-z_][A-Za-z0-9_.-]*)[ \t]*[:=][ \t]*["']?([^"'#\r\n]*)/gim;
    for (const match of content.matchAll(assignment)) {
      const value = match[2].trim();
      if (isCredentialName(match[1]) && !isPlaceholder(value, filePath)) {
        add("credential-text-default", match.index, match[1]);
      }
    }
  }

  const connectionPassword = /(?:Host|Server)=[^;\r\n]+;[^\r\n]*\bPassword\s*=\s*([^;}\r\n]*)/gi;
  for (const match of content.matchAll(connectionPassword)) {
    if (!isPlaceholder(match[1].trim(), filePath)) {
      add("connection-string-password", match.index, "Password");
    }
  }

  for (const [rule, pattern] of [
    ["private-key-material", /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/g],
    ["github-token", /\bgh[pousr]_[A-Za-z0-9]{30,}\b/g],
    ["aws-access-key", /\bAKIA[0-9A-Z]{16}\b/g]
  ]) {
    for (const match of content.matchAll(pattern)) {
      add(rule, match.index);
    }
  }

  return findings;
}

export function scanRepository(repoRoot = process.cwd()) {
  const tracked = execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], {
    cwd: repoRoot,
    encoding: "utf8"
  }).split("\0").filter(Boolean);

  const findings = [];
  for (const relativePath of tracked) {
    if (relativePath === "tools/secret-scan.test.mjs") {
      continue;
    }
    const absolutePath = path.join(repoRoot, relativePath);
    const stats = lstatSync(absolutePath);
    if (!stats.isFile() || stats.size > 5 * 1024 * 1024) {
      continue;
    }

    const buffer = readFileSync(absolutePath);
    if (buffer.includes(0)) {
      continue;
    }

    findings.push(...scanContent(relativePath, buffer.toString("utf8")));
  }
  return findings.sort((left, right) =>
    left.file.localeCompare(right.file) || left.line - right.line || left.rule.localeCompare(right.rule));
}

function scanJson(value, key, visit) {
  if (Array.isArray(value)) {
    for (const item of value) {
      scanJson(item, key, visit);
    }
    return;
  }
  if (value && typeof value === "object") {
    for (const [childKey, childValue] of Object.entries(value)) {
      visit(childKey, childValue);
      scanJson(childValue, childKey, visit);
    }
  }
}

function isCredentialName(name) {
  const normalized = name.replace(/[^a-z0-9]/gi, "").toLowerCase();
  return credentialNames.has(normalized)
    || normalized.endsWith("apikey")
    || normalized.endsWith("password")
    || normalized.endsWith("clientsecret")
    || (normalized.endsWith("privatekey") && !normalized.endsWith("publickey"))
    || normalized.endsWith("presharedkey")
    || normalized.endsWith("encryptionkey");
}

function isPlaceholder(value, filePath) {
  const normalized = value.trim().replace(/^["']|["']$/g, "").toLowerCase();
  if (!normalized || normalized.startsWith("$") || normalized.startsWith("%") || normalized.startsWith("$env:")) {
    return true;
  }
  if (
    normalized.startsWith("test")
    || normalized.startsWith("mock")
    || normalized.startsWith("dummy")
    || normalized.startsWith("example")
    || normalized.startsWith("your-")
    || normalized.startsWith("generate-")
    || normalized.startsWith("replace-")
    || normalized.includes("<redacted>")
  ) {
    return true;
  }
  return filePath.includes("FSTService.Tests/")
    && (normalized.includes("test") || normalized.includes("mock"));
}

function lineNumber(content, index) {
  return content.slice(0, index).split("\n").length;
}

function isMainModule() {
  if (!process.argv[1]) {
    return false;
  }
  return import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
}

if (isMainModule()) {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const findings = scanRepository(repoRoot);
  if (findings.length) {
    for (const finding of findings) {
      const key = finding.key ? ` key=${finding.key}` : "";
      console.error(`${finding.file}:${finding.line} ${finding.rule}${key}`);
    }
    console.error(`Secret scan failed with ${findings.length} finding(s). Values are intentionally not printed.`);
    process.exitCode = 1;
  } else {
    console.log("Secret scan passed: no tracked credential defaults or recognized secret material found.");
  }
}
