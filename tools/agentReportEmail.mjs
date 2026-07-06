import { sendEmailMessage } from "./email.mjs";

export function renderAgentReportEmailMessage(input) {
  const subject = input.subject?.trim() ?? "";
  if (!subject) {
    throw new Error("Agent report e-mail subject is required");
  }

  const body = input.markdown?.trim() || input.text?.trim() || input.html?.trim();
  if (!body) {
    throw new Error("Agent report e-mail body is required");
  }

  const reportHtml = input.html?.trim() || markdownToHtml(input.markdown ?? input.text ?? "");
  return {
    subject,
    html: wrapReportHtml(subject, reportHtml),
    text: input.text ?? input.markdown ?? stripHtml(input.html ?? "")
  };
}

export async function sendAgentReportEmail(input, options = {}) {
  return sendEmailMessage(renderAgentReportEmailMessage(input), options);
}

function wrapReportHtml(subject, reportHtml) {
  const bodyTitle = phaseBodyTitle(subject);
  return `<!doctype html>
<html>
  <head>
    <meta charset="utf-8">
    <style>
      body { margin: 0; padding: 32px; background: #f6f8fa; color: #24292f; font-family: Arial, sans-serif; font-size: 26px; line-height: 1.5; }
      .report { max-width: 1280px; margin: 0 auto; background: #ffffff; border: 1px solid #d0d7de; border-radius: 12px; padding: 32px; }
      h1 { margin: 0 0 24px; font-size: 40px; line-height: 1.2; }
      h2 { margin: 32px 0 16px; font-size: 32px; line-height: 1.25; }
      h3 { margin: 28px 0 14px; font-size: 28px; line-height: 1.25; }
      h4, h5, h6 { margin: 24px 0 12px; font-size: 25px; line-height: 1.25; }
      p { margin: 0 0 18px; }
      ul, ol { margin: 0 0 22px 38px; padding: 0; font-size: 26px; line-height: 1.52; }
      li { margin: 10px 0; }
      li ul, li ol { margin-top: 8px; margin-bottom: 8px; font-size: 25px; }
      .table-sections { margin: 18px 0 28px; }
      .table-row-section { margin: 18px 0 22px; padding: 16px 18px; border: 1px solid #d0d7de; border-radius: 10px; background: #fbfcfd; }
      .table-row-section h4 { margin-top: 0; }
      code { background: #f6f8fa; border-radius: 6px; padding: 2px 6px; font-family: Consolas, monospace; font-size: 0.9em; }
      pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 8px; overflow-x: auto; padding: 16px; font-family: Consolas, monospace; font-size: 20px; line-height: 1.35; }
      blockquote { margin: 18px 0; padding: 10px 20px; border-left: 6px solid #d0d7de; color: #57606a; }
    </style>
  </head>
  <body>
    <div class="report">
      <h1>${inlineMarkdown(bodyTitle)}</h1>
      ${reportHtml}
    </div>
  </body>
</html>`;
}

function markdownToHtml(markdown) {
  const lines = markdown.replace(/\r\n/g, "\n").split("\n");
  const html = [];
  let paragraph = [];
  const listStack = [];
  let inFence = false;
  let fenceLines = [];

  const flushParagraph = () => {
    if (paragraph.length === 0) {
      return;
    }
    html.push(`<p>${inlineMarkdown(paragraph.join(" "))}</p>`);
    paragraph = [];
  };
  const closeAllLists = () => {
    while (listStack.length) {
      const current = listStack.pop();
      if (current.openLi) {
        html.push("</li>");
      }
      html.push(`</${current.type}>`);
    }
  };
  const normalizeIndent = (value) => value.replace(/\t/g, "    ").length;
  const openList = (type, indent) => {
    html.push(`<${type}>`);
    listStack.push({ type, indent, openLi: false });
  };
  const closeListLevel = () => {
    const current = listStack.pop();
    if (!current) {
      return;
    }
    if (current.openLi) {
      html.push("</li>");
    }
    html.push(`</${current.type}>`);
  };
  const appendListItem = (type, indent, value) => {
    flushParagraph();
    if (!listStack.length) {
      openList(type, indent);
    }

    while (listStack.length && indent < listStack[listStack.length - 1].indent) {
      closeListLevel();
    }

    let current = listStack[listStack.length - 1];
    if (indent > current.indent) {
      openList(type, indent);
      current = listStack[listStack.length - 1];
    }

    if (current.indent === indent && current.type !== type) {
      closeListLevel();
      openList(type, indent);
      current = listStack[listStack.length - 1];
    }

    if (current.openLi) {
      html.push("</li>");
    }
    html.push(`<li>${inlineMarkdown(value)}`);
    current.openLi = true;
  };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const trimmed = line.trim();

    if (trimmed.startsWith("```")) {
      if (inFence) {
        html.push(`<pre><code>${escapeHtml(fenceLines.join("\n"))}</code></pre>`);
        fenceLines = [];
        inFence = false;
      } else {
        flushParagraph();
        closeAllLists();
        inFence = true;
      }
      continue;
    }

    if (inFence) {
      fenceLines.push(line);
      continue;
    }

    if (!trimmed) {
      flushParagraph();
      closeAllLists();
      continue;
    }

    if (isTableStart(lines, index)) {
      flushParagraph();
      closeAllLists();
      const parsed = parseTable(lines, index);
      html.push(parsed.html);
      index = parsed.endIndex;
      continue;
    }

    const heading = /^(#{1,6})\s+(.+)$/.exec(trimmed);
    if (heading) {
      flushParagraph();
      closeAllLists();
      const level = Math.min(heading[1].length + 1, 6);
      html.push(`<h${level}>${inlineMarkdown(heading[2])}</h${level}>`);
      continue;
    }

    const unordered = /^(\s*)[-*]\s+(.+)$/.exec(line);
    if (unordered) {
      appendListItem("ul", normalizeIndent(unordered[1]), unordered[2].trim());
      continue;
    }

    const ordered = /^(\s*)\d+\.\s+(.+)$/.exec(line);
    if (ordered) {
      appendListItem("ol", normalizeIndent(ordered[1]), ordered[2].trim());
      continue;
    }

    if (trimmed.startsWith(">")) {
      flushParagraph();
      closeAllLists();
      html.push(`<blockquote>${inlineMarkdown(trimmed.replace(/^>\s?/, ""))}</blockquote>`);
      continue;
    }

    closeAllLists();
    paragraph.push(trimmed);
  }

  if (inFence) {
    html.push(`<pre><code>${escapeHtml(fenceLines.join("\n"))}</code></pre>`);
  }
  flushParagraph();
  closeAllLists();
  return html.join("\n");
}

function isTableStart(lines, index) {
  const current = lines[index]?.trim();
  const next = lines[index + 1]?.trim();
  return Boolean(current?.includes("|") && next && /^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$/.test(next));
}

function parseTable(lines, startIndex) {
  const headers = splitTableRow(lines[startIndex]);
  const bodyRows = [];
  let index = startIndex + 2;
  for (; index < lines.length; index += 1) {
    const trimmed = lines[index].trim();
    if (!trimmed || !trimmed.includes("|")) {
      break;
    }
    bodyRows.push(splitTableRow(trimmed));
  }

  return { html: renderTableAsSections(headers, bodyRows), endIndex: index - 1 };
}

function splitTableRow(row) {
  return row
    .trim()
    .replace(/^\|/, "")
    .replace(/\|$/, "")
    .split("|")
    .map((cell) => cell.trim());
}

function inlineMarkdown(value) {
  let escaped = escapeHtml(value);
  const codeValues = [];
  escaped = escaped.replace(/`([^`]+)`/g, (_match, code) => {
    const token = `@@CODE${codeValues.length}@@`;
    codeValues.push(`<code>${code}</code>`);
    return token;
  });
  escaped = escaped
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/\b_([^_]+)_\b/g, "<em>$1</em>");
  codeValues.forEach((code, index) => {
    escaped = escaped.replace(`@@CODE${index}@@`, code);
  });
  return escaped;
}

function phaseBodyTitle(subject) {
  return subject
    .replace(/^FST Autonomous Agent:\s*/i, "")
    .replace(/\s+[·•]\s+(Accepted|Rejected|Blocked|Mixed|Needs Attention)$/i, "")
    .trim();
}

function renderTableAsSections(headers, bodyRows) {
  if (!bodyRows.length) {
    return "";
  }

  const sectionTitleIndex = chooseSectionTitleIndex(headers);
  const sections = bodyRows.map((row, rowIndex) => {
    const title = row[sectionTitleIndex]?.trim() || `Row ${rowIndex + 1}`;
    const bullets = headers
      .map((header, cellIndex) => ({ header, value: row[cellIndex] ?? "" }))
      .filter(({ value }) => value.trim())
      .map(({ header, value }) => `<li><strong>${inlineMarkdown(header)}</strong>: ${inlineMarkdown(value)}</li>`)
      .join("");
    return `<section class="table-row-section"><h4>${inlineMarkdown(title)}</h4><ul>${bullets}</ul></section>`;
  });
  return `<div class="table-sections">${sections.join("")}</div>`;
}

function chooseSectionTitleIndex(headers) {
  const preferred = ["subject", "task", "metric", "surface", "phase", "file", "before", "run", "work item"];
  const lowerHeaders = headers.map((header) => header.trim().toLowerCase());
  const found = preferred
    .map((name) => lowerHeaders.findIndex((header) => header === name || header.includes(name)))
    .find((index) => index >= 0);
  return found ?? 0;
}

function escapeHtml(value) {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function stripHtml(value) {
  return value
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
    .trim();
}
