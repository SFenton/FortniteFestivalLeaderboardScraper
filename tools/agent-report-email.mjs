#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import { sendAgentReportEmail } from "./agentReportEmail.mjs";
import { testEmailSubjectPrefix } from "./email.mjs";

const args = parseArgs(process.argv.slice(2));

try {
  const subject = requiredValue(args.values.subject, "Missing --subject <subject>");
  const markdown = args.values["input-md"] ? await readFile(args.values["input-md"], "utf8") : args.values["body-md"];
  const html = args.values["input-html"] ? await readFile(args.values["input-html"], "utf8") : args.values["body-html"];
  const text = args.values["body-text"];
  if (!markdown && !html && !text) {
    throw new Error("Missing --input-md <path>, --input-html <path>, --body-md, --body-html, or --body-text");
  }

  const result = await sendAgentReportEmail(
    {
      subject,
      markdown,
      html,
      text
    },
    {
      dryRun: args.flags.has("send") ? false : true,
      enabled: args.flags.has("send") ? true : undefined,
      outboxDir: args.values["outbox-dir"],
      subjectPrefix: args.flags.has("test") ? testEmailSubjectPrefix() : args.values["subject-prefix"]
    }
  );
  console.log(JSON.stringify(result, null, 2));
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  console.error("Usage: node tools/agent-report-email.mjs --subject <subject> --input-md <report.md> [--send] [--test] [--outbox-dir <dir>]");
  process.exitCode = 64;
}

function parseArgs(argv) {
  const flags = new Set();
  const values = {};

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      continue;
    }

    const key = token.slice(2);
    const next = argv[index + 1];
    if (next && !next.startsWith("--")) {
      values[key] = next;
      index += 1;
    } else {
      flags.add(key);
    }
  }

  return { flags, values };
}

function requiredValue(value, message) {
  if (!value?.trim()) {
    throw new Error(message);
  }
  return value;
}
