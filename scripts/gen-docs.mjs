#!/usr/bin/env node
// gen-docs.mjs — Regenerate auto-derivable reference docs from source.
//
// Currently generates: docs/reference/mcp-tools.md  (the MCP tool index)
// Source of truth:      apps/Agentweaver.Mcp/Tools/*.cs  ([McpServerTool] attributes)
//
// Usage:
//   node scripts/gen-docs.mjs            # write the generated file(s)
//   node scripts/gen-docs.mjs --check    # exit 1 if the committed file is stale (CI)
//
// This is intentionally dependency-free (no npm install) so it runs anywhere
// Node is available, including in CI before `npm ci` in docs/.

import { readFileSync, writeFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..");
const toolsDir = join(repoRoot, "apps", "Agentweaver.Mcp", "Tools");
const outFile = join(repoRoot, "docs", "reference", "mcp-tools.md");

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

// ── Build the generated markdown ─────────────────────────────────────────────

function build() {
  const files = readdirSync(toolsDir)
    .filter((f) => f.endsWith("Tools.cs"))
    .sort();

  const groups = files
    .map((f) => parseToolsFile(join(toolsDir, f)))
    .filter((g) => g.tools.length > 0)
    .sort((a, b) => a.category.localeCompare(b.category));

  const total = groups.reduce((n, g) => n + g.tools.length, 0);

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
    for (const t of g.tools.sort((a, b) => a.name.localeCompare(b.name))) {
      lines.push(`| \`${t.name}\` | ${t.description} |`);
    }
    lines.push("");
  }

  return lines.join("\n") + "\n";
}

// ── Main ─────────────────────────────────────────────────────────────────────

const check = process.argv.includes("--check");
const generated = build();
const rel = relative(repoRoot, outFile).replace(/\\/g, "/");

if (check) {
  let current = "";
  try {
    current = readFileSync(outFile, "utf8");
  } catch {
    /* missing file counts as drift */
  }
  if (current !== generated) {
    console.error(
      `DRIFT: ${rel} is out of date. Run 'node scripts/gen-docs.mjs' and commit the result.`
    );
    process.exit(1);
  }
  console.log(`OK: ${rel} is in sync.`);
} else {
  writeFileSync(outFile, generated);
  console.log(`Wrote ${rel}`);
}
