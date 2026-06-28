#!/usr/bin/env node
// gen-docs.mjs — Regenerate auto-derivable reference docs from source.
//
// Generates, from the single source of truth apps/Agentweaver.Mcp/Tools/*.cs:
//   1. docs/reference/mcp-tools.md                       — the full MCP tool index.
//   2. .github/agents/agentweaver.agent.md               — only the "## Tool map"
//      block (delimited by <!-- BEGIN/END GENERATED:tool-map -->); ALL other prose
//      in that file is hand-written and preserved verbatim.
//   3. apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md — a byte-for-byte
//      copy of (2), embedded into Agentweaver.Api and materialized into each new
//      project's .github/agents/ at creation time. Keeping it a generated copy means
//      the repo file and the per-project template can never drift.
//
// Usage:
//   node scripts/gen-docs.mjs            # write the generated file(s)
//   node scripts/gen-docs.mjs --check    # exit 1 if any committed file is stale (CI)
//
// This is intentionally dependency-free (no npm install) so it runs anywhere
// Node is available, including in CI before `npm ci` in docs/.

import { readFileSync, writeFileSync, readdirSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..");
const toolsDir = join(repoRoot, "apps", "Agentweaver.Mcp", "Tools");
const toolsDocFile = join(repoRoot, "docs", "reference", "mcp-tools.md");
const agentFile = join(repoRoot, ".github", "agents", "agentweaver.agent.md");
const agentTemplateCopy = join(
  repoRoot,
  "apps",
  "Agentweaver.Api",
  "Projects",
  "Templates",
  "agentweaver.agent.md"
);

// Markers delimiting the only generated region inside the hand-written agent file.
const TOOL_MAP_BEGIN = "<!-- BEGIN GENERATED:tool-map -->";
const TOOL_MAP_END = "<!-- END GENERATED:tool-map -->";

// ── C# string-literal helpers ────────────────────────────────────────────────

// Extract the concatenated value of one or more adjacent C# string literals
// starting at `text[start]` (which must be the opening quote). Handles `"a" + "b"`
// concatenation and basic escapes. Returns { value, end }.
function readConcatenatedString(text, start) {
  let i = start;
  let value = "";
  while (i < text.length) {
    // Skip whitespace and concatenation operators between literals.
    while (i < text.length && /[\s+]/.test(text[i])) i++;
    if (text[i] !== '"') break;
    i++; // consume opening quote
    while (i < text.length && text[i] !== '"') {
      if (text[i] === "\\") {
        const next = text[i + 1];
        const map = { n: "\n", t: "\t", r: "\r", '"': '"', "\\": "\\" };
        value += map[next] ?? next ?? "";
        i += 2;
      } else {
        value += text[i];
        i++;
      }
    }
    i++; // consume closing quote
  }
  return { value, end: i };
}

// ── Parse one Tools/*.cs file into { category, tools: [{name, description}] } ──

function parseToolsFile(filePath) {
  const text = readFileSync(filePath, "utf8");
  const base = filePath.split(/[\\/]/).pop().replace(/Tools\.cs$/, "");
  const category = base
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/\bGit Hub\b/, "GitHub");

  const tools = [];
  const nameRe = /McpServerTool\(\s*Name\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = nameRe.exec(text)) !== null) {
    const name = m[1];
    const descIdx = text.indexOf("Description(", m.index);
    let description = "";
    if (descIdx !== -1) {
      const quoteIdx = text.indexOf('"', descIdx);
      if (quoteIdx !== -1) {
        description = readConcatenatedString(text, quoteIdx).value;
      }
    }
    tools.push({ name, description: normalize(description) });
  }
  return { category, tools };
}

function normalize(s) {
  return s.replace(/\s+/g, " ").trim().replace(/\|/g, "\\|");
}

// ── Shared parse: the canonical category groups (source of truth) ─────────────

function parseGroups() {
  const files = readdirSync(toolsDir)
    .filter((f) => f.endsWith("Tools.cs"))
    .sort();

  const groups = files
    .map((f) => parseToolsFile(join(toolsDir, f)))
    .filter((g) => g.tools.length > 0)
    .sort((a, b) => a.category.localeCompare(b.category));

  for (const g of groups) {
    g.tools.sort((a, b) => a.name.localeCompare(b.name));
  }

  const total = groups.reduce((n, g) => n + g.tools.length, 0);
  return { groups, total };
}

// ── Build the generated mcp-tools.md ─────────────────────────────────────────

function buildToolsDoc(groups, total) {
  const lines = [];
  lines.push("<!--");
  lines.push("  GENERATED FILE — DO NOT EDIT BY HAND.");
  lines.push("  Source: apps/Agentweaver.Mcp/Tools/*.cs ([McpServerTool] attributes)");
  lines.push("  Regenerate: node scripts/gen-docs.mjs");
  lines.push("  CI verifies this file is in sync (.github/workflows/docs-drift.yml).");
  lines.push("-->");
  lines.push("# MCP tool index");
  lines.push("");
  lines.push(
    "::: warning Generated"
  );
  lines.push(
    "This page is generated from the MCP server source. Do not edit it by hand — run `node scripts/gen-docs.mjs`. For the full parameter reference of each tool, see [MCP server reference](./mcp.md)."
  );
  lines.push(":::");
  lines.push("");
  lines.push(
    `The Agentweaver MCP server exposes **${total} tools** across **${groups.length} categories**. This index is the authoritative list of tool names and one-line descriptions, derived directly from the \`[McpServerTool]\` attributes in the server source.`
  );
  lines.push("");

  for (const g of groups) {
    lines.push(`## ${g.category}`);
    lines.push("");
    lines.push("| Tool | Description |");
    lines.push("| --- | --- |");
    for (const t of g.tools) {
      lines.push(`| \`${t.name}\` | ${t.description} |`);
    }
    lines.push("");
  }

  return lines.join("\n") + "\n";
}

// ── Build the generated "## Tool map" block for the agent definition ──────────
//
// Compact, name-only listing grouped by the SAME categories as mcp-tools.md, so
// the agent file and the reference doc never disagree about the tool set.

function buildToolMapBlock(groups, total) {
  const lines = [];
  lines.push(TOOL_MAP_BEGIN);
  lines.push("<!--");
  lines.push("  GENERATED BLOCK — DO NOT EDIT BY HAND.");
  lines.push("  Source: apps/Agentweaver.Mcp/Tools/*.cs ([McpServerTool] attributes)");
  lines.push("  Regenerate: node scripts/gen-docs.mjs");
  lines.push("  Everything outside the BEGIN/END markers is hand-written and preserved.");
  lines.push("-->");
  lines.push("");
  lines.push(
    `The Agentweaver MCP server exposes **${total} tools** across **${groups.length} categories**. Tool names below are the stable identifiers to call (each is the \`agentweaver-*\` MCP tool); one-line descriptions live in \`docs/reference/mcp-tools.md\`.`
  );
  lines.push("");
  for (const g of groups) {
    const names = g.tools.map((t) => `\`${t.name}\``).join(", ");
    lines.push(`- **${g.category}:** ${names}`);
  }
  lines.push("");
  lines.push(TOOL_MAP_END);
  return lines.join("\n");
}

// Replace the region between the markers (inclusive) with a freshly built block,
// preserving every other byte of the hand-written template verbatim.
function applyToolMapBlock(templateText, block) {
  const beginIdx = templateText.indexOf(TOOL_MAP_BEGIN);
  const endIdx = templateText.indexOf(TOOL_MAP_END);
  if (beginIdx === -1 || endIdx === -1 || endIdx < beginIdx) {
    throw new Error(
      `Agent template ${relative(repoRoot, agentFile).replace(/\\/g, "/")} is missing the ` +
        `'${TOOL_MAP_BEGIN}' / '${TOOL_MAP_END}' markers around the Tool map section.`
    );
  }
  const before = templateText.slice(0, beginIdx);
  const after = templateText.slice(endIdx + TOOL_MAP_END.length);
  return before + block + after;
}

// ── Targets ──────────────────────────────────────────────────────────────────

function computeTargets() {
  const { groups, total } = parseGroups();

  // 1. mcp-tools.md
  const toolsDoc = buildToolsDoc(groups, total);

  // 2. agent file: regenerate ONLY the tool-map block from the current (committed)
  //    template, so hand-written prose is the live template and stays verbatim.
  let agentTemplate = "";
  try {
    agentTemplate = readFileSync(agentFile, "utf8");
  } catch {
    throw new Error(
      `Missing agent template ${relative(repoRoot, agentFile).replace(/\\/g, "/")}; ` +
        `it must exist with the tool-map markers.`
    );
  }
  // Normalize CRLF so generated output is deterministic regardless of git autocrlf.
  agentTemplate = agentTemplate.replace(/\r\n/g, "\n");
  const block = buildToolMapBlock(groups, total);
  const agentContent = applyToolMapBlock(agentTemplate, block);

  // 3. API embedded copy: identical bytes to the agent file.
  return [
    { file: toolsDocFile, content: toolsDoc },
    { file: agentFile, content: agentContent },
    { file: agentTemplateCopy, content: agentContent },
  ];
}

// ── Main ─────────────────────────────────────────────────────────────────────

const check = process.argv.includes("--check");
const targets = computeTargets();

if (check) {
  let drift = false;
  for (const { file, content } of targets) {
    const rel = relative(repoRoot, file).replace(/\\/g, "/");
    let current = "";
    try {
      current = readFileSync(file, "utf8").replace(/\r\n/g, "\n");
    } catch {
      /* missing file counts as drift */
    }
    if (current !== content) {
      console.error(
        `DRIFT: ${rel} is out of date. Run 'node scripts/gen-docs.mjs' and commit the result.`
      );
      drift = true;
    } else {
      console.log(`OK: ${rel} is in sync.`);
    }
  }
  if (drift) process.exit(1);
} else {
  for (const { file, content } of targets) {
    const rel = relative(repoRoot, file).replace(/\\/g, "/");
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, content);
    console.log(`Wrote ${rel}`);
  }
}
