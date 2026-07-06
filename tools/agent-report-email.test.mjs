import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { renderAgentReportEmailMessage, sendAgentReportEmail } from "./agentReportEmail.mjs";

describe("agent report email", () => {
  it("renders markdown phase reports as readable human-friendly HTML", () => {
    const message = renderAgentReportEmailMessage({
      subject: "FST Autonomous Agent: Phase 1 - Setup · Accepted",
      markdown: `## Phase summary

| Task | Decision | Runtime |
|---|---|---:|
| Build skill | **accepted** | 10s |

- Wrote \`SKILL.md\`
  - Nested detail for artifact explanation
- Escaped <script>alert("x")</script>
`
    });

    assert.equal(message.subject, "FST Autonomous Agent: Phase 1 - Setup · Accepted");
    assert.match(message.html, /<h1>Phase 1 - Setup<\/h1>/);
    assert.doesNotMatch(message.html, /<h1>FST Autonomous Agent:/);
    assert.match(message.html, /<section class="table-row-section"><h4>Build skill<\/h4><ul>/);
    assert.match(message.html, /<li><strong>Task<\/strong>: Build skill<\/li>/);
    assert.match(message.html, /<li><strong>Decision<\/strong>: <strong>accepted<\/strong><\/li>/);
    assert.doesNotMatch(message.html, /<table>/);
    assert.match(message.html, /<code>SKILL\.md<\/code>/);
    assert.match(message.html, /<li>Wrote <code>SKILL\.md<\/code>/);
    assert.match(message.html, /<ul>\n<li>Nested detail for artifact explanation/);
    assert.match(message.html, /&lt;script&gt;alert\(&quot;x&quot;\)&lt;\/script&gt;/);
    assert.doesNotMatch(message.html, /<script>alert/);
    assert.match(message.text, /\| Task \| Decision \| Runtime \|/);
  });

  it("writes dry-run phase reports through the shared email sender", async () => {
    const outbox = await mkdtemp(path.join(tmpdir(), "fst-agent-report-email-"));
    try {
      const result = await sendAgentReportEmail(
        {
          subject: "FST Autonomous Agent: Phase 1 - Report · Accepted",
          markdown: "## Done\n\n| Task | Result |\n|---|---|\n| Report | accepted |"
        },
        { outboxDir: outbox }
      );

      assert.equal(result.sent, false);
      assert.equal(result.dryRun, true);
      assert.equal(result.reason, "dry_run");
      assert.match(await readFile(result.outboxHtmlPath, "utf8"), /Phase 1 - Report/);
      assert.match(await readFile(result.outboxHtmlPath, "utf8"), /class="table-row-section"/);
    } finally {
      await rm(outbox, { recursive: true, force: true });
    }
  });
});
