import test from "node:test";
import assert from "node:assert/strict";
import { isWorkingTreeClean, parseArgs, validateMainSha, run } from "../release-publish.mjs";

const mirrors = new Map([["/repo/VERSION", "0.9.70\n"], ["/repo/package.json", '{"version":"0.9.70"}'], ["/repo/package-lock.json", '{"version":"0.9.70","packages":{"":{"version":"0.9.70"}}}'], ["/repo/CHANGELOG.md", "## 0.9.70\n\n- Prepared release note\n"]]);
const readMirror = (file) => mirrors.get(file.replaceAll("\\", "/"));
const log = { info() {}, section() {}, field() {}, ok() {}, skip() {}, warn() {}, error() {}, debug() {}, command() {} };
function fakeExec({
  wrongMain = false,
  tag = false,
  untracked = false,
  ignoredStatus = "",
  unexpectedIgnored = false,
} = {}) {
  const calls = []; return { calls, setDryRun() {}, async run(cmd, args) { calls.push({ cmd, args }); return { code: 0 }; }, async capture(cmd, args) {
    calls.push({ cmd, args });
    if (args[0] === "diff") return { code: 0, stdout: "" };
    if (args[0] === "status" && args.includes("--untracked-files=all")) return { code: 0, stdout: untracked ? "?? poisoned-source.js\n" : "" };
    if (args[0] === "status" && args.includes("--ignored=matching")) return { code: 0, stdout: unexpectedIgnored ? "!! malicious.js\n" : ignoredStatus };
    if (args[0] === "rev-parse" && args[1] === "HEAD") return { code: 0, stdout: "abc" };
    if (args[0] === "rev-parse" && args[1] === "origin/main") return { code: 0, stdout: wrongMain ? "def" : "abc" };
    if (args[0] === "rev-parse") return { code: tag ? 0 : 1, stdout: "" };
    if (args[0] === "cat-file") return { code: tag ? 0 : 1, stdout: tag ? "tag" : "" };
    if (args[0] === "rev-list") return { code: 0, stdout: "abc" };
    if (args[0] === "tag") return { code: 0, stdout: "v0.9.69\n" };
    if (cmd === "gh") return { code: 1, stdout: "" };
    return { code: 0, stdout: "" };
  } };
}
test("publish accepts only dry-run and resume options", () => { assert.deepEqual(parseArgs(["--dry-run"]), { resumeTag: undefined, dryRun: true, help: false }); assert.throws(() => parseArgs(["patch"]), /Unknown argument/); });
test("publish rejects untracked files when checking working tree cleanliness", async () => {
  const exec = fakeExec({ untracked: true });
  assert.equal(await isWorkingTreeClean({ cwd: "/repo", capture: exec.capture }), false);
  assert.ok(exec.calls.some((call) => call.cmd === "git" && call.args.join(" ") === "status --porcelain --untracked-files=all"));
});
test("publish rejects unexpected ignored files when checking working tree cleanliness", async () => {
  const exec = fakeExec({ unexpectedIgnored: true });
  assert.equal(await isWorkingTreeClean({ cwd: "/repo", capture: exec.capture }), false);
  assert.ok(exec.calls.some((call) => call.cmd === "git" && call.args.join(" ") === "status --porcelain --ignored=matching"));
});
test("publish accepts standard built-checkout and harness outputs through the shared policy", async () => {
  const ignoredStatus = [
    "!! NODE_MODULES\\",
    "!! apps\\web\\DIST\\",
    "!! packages\\Agentweaver.Domain\\OBJ\\",
    "!! packages\\Agentweaver.AgentRuntime\\BIN\\Release\\",
    "!! scripts\\API-HARNESS\\findings\\run.json",
  ].join("\r\n");
  assert.equal(await isWorkingTreeClean({ cwd: "C:\\repo", capture: fakeExec({ ignoredStatus }).capture }), true);
});
test("publish still rejects ignored source paths after Windows normalization", async () => {
  const ignoredStatus = "!! SRC\\Backdoor.TS\r\n";
  assert.equal(await isWorkingTreeClean({ cwd: "C:\\repo", capture: fakeExec({ ignoredStatus }).capture }), false);
});
test("publish refuses untracked files before creating a release", async () => {
  await assert.rejects(
    run({ repoRoot: "/repo", exec: fakeExec({ untracked: true }), log, readFile: readMirror }),
    /Working tree has uncommitted changes/,
  );
});
test("publish rejects mirror mismatch before publication", async () => { const exec = fakeExec(); const readFile = (file) => file.endsWith("package.json") ? '{"version":"0.9.69"}' : readMirror(file); await assert.rejects(run({ repoRoot: "/repo", exec, log, readFile }), /mirrors disagree/); });
test("publish requires the exact origin/main SHA", async () => { await assert.rejects(run({ repoRoot: "/repo", exec: fakeExec({ wrongMain: true }), log, readFile: readMirror }), /exact fetched origin\/main SHA/); });
test("publish requires a matching changelog section", async () => { const readFile = (file) => file.endsWith("CHANGELOG.md") ? "# no release" : readMirror(file); await assert.rejects(run({ repoRoot: "/repo", exec: fakeExec(), log, readFile }), /no section/); });
test("publish tags and uses extracted changelog notes without writing version files", async () => { const exec = fakeExec(); const result = await run({ repoRoot: "/repo", exec, log, readFile: readMirror }); assert.equal(result.tag, "v0.9.70"); assert.equal(result.changelog, "## 0.9.70\n\n- Prepared release note"); assert.ok(exec.calls.some((c) => c.cmd === "git" && c.args[0] === "tag")); assert.ok(exec.calls.some((c) => c.cmd === "gh" && c.args.includes("--notes"))); assert.ok(!exec.calls.some((c) => c.args?.includes("commit") || c.args?.includes("add"))); });
test("publish dry-run does not create a tag", async () => { const exec = fakeExec(); await run({ argv: ["--dry-run"], repoRoot: "/repo", exec, log, readFile: readMirror }); assert.ok(!exec.calls.some((c) => c.cmd === "git" && c.args[0] === "tag" && c.args[1] === "-a")); });
test("resume creates a missing GitHub Release for an existing tag", async () => { const exec = fakeExec({ tag: true }); const result = await run({ argv: ["--resume", "v0.9.70"], repoRoot: "/repo", exec, log, readFile: readMirror }); assert.equal(result.ok, true); assert.ok(exec.calls.some((c) => c.cmd === "gh" && c.args[0] === "release")); });
