import { mkdir, readFile, writeFile } from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import tls from "node:tls";

const defaultSender = "fst-autonomous-agent@example.invalid";
const defaultRecipient = "operator@example.invalid";
const fallbackEmailEnvironmentMap = new Map([
  ["DAY_TRADER_EMAIL_ENABLED", "FST_AUTONOMOUS_EMAIL_ENABLED"],
  ["DAY_TRADER_EMAIL_DRY_RUN", "FST_AUTONOMOUS_EMAIL_DRY_RUN"],
  ["DAY_TRADER_EMAIL_FROM", "FST_AUTONOMOUS_EMAIL_FROM"],
  ["DAY_TRADER_EMAIL_TO", "FST_AUTONOMOUS_EMAIL_TO"],
  ["DAY_TRADER_EMAIL_SMTP_HOST", "FST_AUTONOMOUS_EMAIL_SMTP_HOST"],
  ["DAY_TRADER_EMAIL_SMTP_PORT", "FST_AUTONOMOUS_EMAIL_SMTP_PORT"],
  ["DAY_TRADER_EMAIL_SMTP_SECURE", "FST_AUTONOMOUS_EMAIL_SMTP_SECURE"],
  ["DAY_TRADER_EMAIL_SMTP_USER", "FST_AUTONOMOUS_EMAIL_SMTP_USER"],
  ["DAY_TRADER_EMAIL_SMTP_PASSWORD", "FST_AUTONOMOUS_EMAIL_SMTP_PASSWORD"]
]);

export function testEmailSubjectPrefix(asOf = new Date()) {
  return `[TEST] ${asOf.toISOString()}`;
}

export async function loadEmailEnvironmentFallback(filePath, environment = process.env) {
  const contents = await readFile(filePath, "utf8");
  applyEmailEnvironmentFallback(contents, environment);
}

export function applyEmailEnvironmentFallback(contents, environment = process.env) {
  for (const line of contents.replace(/\r\n/g, "\n").split("\n")) {
    const match = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line.trim());
    if (!match) {
      continue;
    }

    const targetName = fallbackEmailEnvironmentMap.get(match[1]);
    if (!targetName || environment[targetName]) {
      continue;
    }

    environment[targetName] = parseDotEnvValue(match[2]);
  }
}

function parseDotEnvValue(rawValue) {
  const value = rawValue.trim();
  if (value.length >= 2 && value.startsWith("\"") && value.endsWith("\"")) {
    return value
      .slice(1, -1)
      .replace(/\\n/g, "\n")
      .replace(/\\r/g, "\r")
      .replace(/\\t/g, "\t")
      .replace(/\\"/g, "\"")
      .replace(/\\\\/g, "\\");
  }
  if (value.length >= 2 && value.startsWith("'") && value.endsWith("'")) {
    return value.slice(1, -1);
  }
  return value.replace(/\s+#.*$/, "").trim();
}

export async function sendEmailMessage(message, options = {}) {
  const dryRun = options.dryRun ?? process.env.FST_AUTONOMOUS_EMAIL_DRY_RUN !== "false";
  const resolved = resolveEmailMessage(message, options.subjectPrefix, dryRun);

  if (dryRun) {
    const outbox = await writeDryRunEmail(resolved, options.outboxDir);
    return {
      sent: false,
      dryRun: true,
      from: resolved.from,
      to: resolved.to,
      subject: resolved.subject,
      ...outbox,
      reason: "dry_run"
    };
  }

  const enabled = options.enabled ?? process.env.FST_AUTONOMOUS_EMAIL_ENABLED === "true";
  if (!enabled) {
    return {
      sent: false,
      dryRun: false,
      from: resolved.from,
      to: resolved.to,
      subject: resolved.subject,
      reason: "FST_AUTONOMOUS_EMAIL_ENABLED is not true; refusing to send email"
    };
  }

  const smtp = smtpConfig(resolved.from);
  const messageId = await sendSmtpMail(resolved, smtp);
  return {
    sent: true,
    dryRun: false,
    from: resolved.from,
    to: resolved.to,
    subject: resolved.subject,
    messageId
  };
}

function resolveEmailMessage(message, subjectPrefix, dryRun) {
  const from = message.from?.trim() || process.env.FST_AUTONOMOUS_EMAIL_FROM?.trim() || defaultSender;
  const explicitRecipients = message.to ?? process.env.FST_AUTONOMOUS_EMAIL_TO;
  if (!dryRun && !explicitRecipients) {
    throw new Error("FST_AUTONOMOUS_EMAIL_TO is required for real e-mail sends");
  }

  const to = parseRecipients(explicitRecipients ?? defaultRecipient);
  if (to.length === 0) {
    throw new Error("E-mail recipient is required");
  }

  const subject = prefixedSubject(message.subject?.trim() ?? "", subjectPrefix);
  if (!subject) {
    throw new Error("E-mail subject is required");
  }
  if (!message.html?.trim()) {
    throw new Error("E-mail HTML body is required");
  }

  return {
    from,
    to,
    subject,
    html: message.html,
    text: message.text ?? htmlToText(message.html),
    attachments: message.attachments ?? []
  };
}

function prefixedSubject(subject, prefix) {
  const normalizedPrefix = prefix?.trim();
  if (!normalizedPrefix || subject.startsWith(normalizedPrefix)) {
    return subject;
  }
  return `${normalizedPrefix} ${subject}`;
}

function parseRecipients(input) {
  const values = Array.isArray(input) ? input : String(input).split(",");
  return values.map((value) => value.trim()).filter(Boolean);
}

function smtpConfig(from) {
  const port = Number(process.env.FST_AUTONOMOUS_EMAIL_SMTP_PORT ?? 465);
  if (!Number.isInteger(port) || port <= 0) {
    throw new Error("FST_AUTONOMOUS_EMAIL_SMTP_PORT must be a positive integer");
  }

  const user = process.env.FST_AUTONOMOUS_EMAIL_SMTP_USER?.trim() || from;
  const password = process.env.FST_AUTONOMOUS_EMAIL_SMTP_PASSWORD;
  if (!password) {
    throw new Error("FST_AUTONOMOUS_EMAIL_SMTP_PASSWORD is required when FST_AUTONOMOUS_EMAIL_DRY_RUN=false");
  }

  return {
    host: process.env.FST_AUTONOMOUS_EMAIL_SMTP_HOST?.trim() || "smtp.gmail.com",
    port,
    secure: process.env.FST_AUTONOMOUS_EMAIL_SMTP_SECURE !== "false",
    user,
    password
  };
}

async function writeDryRunEmail(message, outboxDir) {
  const dir = outboxDir ?? process.env.FST_AUTONOMOUS_EMAIL_OUTBOX_DIR ?? path.join(process.cwd(), ".outbox/fst-autonomous-agent");
  await mkdir(dir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const basename = `${stamp}-${slug(message.subject)}`;
  const outboxHtmlPath = path.join(dir, `${basename}.html`);
  const outboxJsonPath = path.join(dir, `${basename}.json`);
  const outboxAttachmentPaths = message.attachments.map((attachment, index) =>
    path.join(dir, `${basename}-attachment-${index + 1}-${attachmentOutboxName(attachment.filename)}`)
  );

  await Promise.all([
    writeFile(outboxHtmlPath, message.html, "utf8"),
    ...message.attachments.map((attachment, index) => writeFile(outboxAttachmentPaths[index], attachment.content)),
    writeFile(
      outboxJsonPath,
      JSON.stringify(
        {
          from: message.from,
          to: message.to,
          subject: message.subject,
          text: message.text,
          htmlPath: outboxHtmlPath,
          attachments: message.attachments.map((attachment, index) => ({
            filename: attachment.filename,
            contentType: attachment.contentType,
            cid: attachment.cid,
            path: outboxAttachmentPaths[index]
          }))
        },
        null,
        2
      ),
      "utf8"
    )
  ]);

  return { outboxHtmlPath, outboxJsonPath, outboxAttachmentPaths };
}

async function sendSmtpMail(message, smtp) {
  const socket = smtp.secure
    ? tls.connect({ host: smtp.host, port: smtp.port, servername: smtp.host })
    : net.connect({ host: smtp.host, port: smtp.port });
  socket.setEncoding("utf8");

  try {
    await readSmtpResponse(socket, [220]);
    await smtpCommand(socket, `EHLO ${smtp.host}`, [250]);
    await smtpCommand(socket, "AUTH LOGIN", [334]);
    await smtpCommand(socket, Buffer.from(smtp.user).toString("base64"), [334]);
    await smtpCommand(socket, Buffer.from(smtp.password).toString("base64"), [235]);
    await smtpCommand(socket, `MAIL FROM:<${message.from}>`, [250]);
    for (const recipient of message.to) {
      await smtpCommand(socket, `RCPT TO:<${recipient}>`, [250, 251]);
    }
    await smtpCommand(socket, "DATA", [354]);
    const messageId = `<fst-autonomous-${Date.now()}-${Math.random().toString(36).slice(2)}@local>`;
    socket.write(`${buildMimeMessage(message, messageId)}\r\n.\r\n`);
    await readSmtpResponse(socket, [250]);
    await smtpCommand(socket, "QUIT", [221]);
    return messageId;
  } finally {
    socket.end();
  }
}

function buildMimeMessage(message, messageId) {
  const boundary = `fst-boundary-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const headers = [
    `From: ${message.from}`,
    `To: ${message.to.join(", ")}`,
    `Subject: ${encodeHeader(message.subject)}`,
    `Message-ID: ${messageId}`,
    "MIME-Version: 1.0",
    `Content-Type: multipart/alternative; boundary="${boundary}"`
  ];

  return [
    ...headers,
    "",
    `--${boundary}`,
    "Content-Type: text/plain; charset=utf-8",
    "Content-Transfer-Encoding: 8bit",
    "",
    dotStuff(message.text),
    `--${boundary}`,
    "Content-Type: text/html; charset=utf-8",
    "Content-Transfer-Encoding: 8bit",
    "",
    dotStuff(message.html),
    `--${boundary}--`
  ].join("\r\n");
}

function smtpCommand(socket, command, expectedCodes) {
  socket.write(`${command}\r\n`);
  return readSmtpResponse(socket, expectedCodes);
}

function readSmtpResponse(socket, expectedCodes) {
  return new Promise((resolve, reject) => {
    let buffer = "";
    const cleanup = () => {
      socket.off("data", onData);
      socket.off("error", onError);
      socket.off("end", onEnd);
    };
    const onError = (error) => {
      cleanup();
      reject(error);
    };
    const onEnd = () => {
      cleanup();
      reject(new Error("SMTP connection ended before a complete response"));
    };
    const onData = (chunk) => {
      buffer += chunk;
      const lines = buffer.split(/\r?\n/).filter(Boolean);
      const last = lines.at(-1);
      const match = /^(\d{3})([\s-])/.exec(last ?? "");
      if (!match || match[2] === "-") {
        return;
      }
      cleanup();
      const code = Number(match[1]);
      if (!expectedCodes.includes(code)) {
        reject(new Error(`SMTP command failed with ${code}: ${lines.join(" | ")}`));
        return;
      }
      resolve(lines.join("\n"));
    };
    socket.on("data", onData);
    socket.on("error", onError);
    socket.on("end", onEnd);
  });
}

function slug(value) {
  const cleaned = value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 80);
  return cleaned || "email";
}

function attachmentOutboxName(filename) {
  const extension = path.extname(filename);
  const stem = extension ? filename.slice(0, -extension.length) : filename;
  const safeExtension = extension.toLowerCase().replace(/[^.a-z0-9]+/g, "");
  return `${slug(stem)}${safeExtension}`;
}

function htmlToText(html) {
  return html
    .replace(/<style[\s\S]*?<\/style>/gi, "")
    .replace(/<script[\s\S]*?<\/script>/gi, "")
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<\/(p|div|li|tr|h[1-6])>/gi, "\n")
    .replace(/<[^>]+>/g, "")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, "\"")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

function encodeHeader(value) {
  return /[^\x20-\x7e]/.test(value) ? `=?UTF-8?B?${Buffer.from(value).toString("base64")}?=` : value;
}

function dotStuff(value) {
  return value.replace(/^\./gm, "..");
}
