import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { scanContent } from "./secret-scan.mjs";

describe("secret scan", () => {
  it("finds credential-like defaults without returning their values", () => {
    const findings = [
      ...scanContent("FSTService/appsettings.json", '{"Api":{"ApiKey":"non-placeholder-value"}}'),
      ...scanContent("FSTService/Auth/Auth.cs", 'const string DefaultClientSecret = "non-placeholder-secret";'),
      ...scanContent("docker-compose.yml", "WEBAPP_PASSWORD=${WEBAPP_PASSWORD:-changeme}")
    ];

    assert.deepEqual(
      findings.map(({ rule }) => rule).sort(),
      [
        "credential-code-default",
        "credential-interpolation-default",
        "credential-json-default"
      ].sort()
    );
    assert.ok(findings.every((finding) => !Object.hasOwn(finding, "value")));
  });

  it("allows empty, interpolated, and test-only placeholders", () => {
    const findings = [
      ...scanContent("FSTService/appsettings.json", '{"Api":{"ApiKey":""}}'),
      ...scanContent("docker-compose.yml", "PG_PASSWORD=${PG_PASSWORD:?Set PG_PASSWORD in .env}"),
      ...scanContent("FSTService/.env.example", "EPIC_CLIENT_SECRET=your-epic-client-secret"),
      ...scanContent("FSTService.Tests/AuthTests.cs", 'const string ClientSecret = "test-client-secret";')
    ];

    assert.deepEqual(findings, []);
  });
});
