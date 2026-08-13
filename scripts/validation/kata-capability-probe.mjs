#!/usr/bin/env node
// Capability probe for the AgentHost executor sidecar (#757 scope correction).
//
// Runs INSIDE the AgentHost container of a sandbox pod and exercises the *developer* workloads the
// executor is required to support, using the real executor protocol (NDJSON over the pod-private
// unix socket) — no test hooks, no mocks.
//
// Usage (inside the container):
//   node kata-capability-probe.mjs <case> [runRoot]
//
// Cases: npm | nuget | apt | apt-isolation | buildkit | preview | nested-userns | negative
//        | capabilities

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const SOCKET = process.env.AGENTWEAVER_EXEC_SOCKET || '/var/run/agentweaver-exec/exec.sock';
const TOKEN = fs.readFileSync(path.join(path.dirname(SOCKET), 'exec.token'), 'utf8').trim();
const CASE = process.argv[2] || 'npm';
const RUN_ROOT = process.argv[3] || '/local-workspace/capability';
const RUN_A = path.join(RUN_ROOT, 'run-a');
const HOME_A = path.join(RUN_ROOT, 'home-a');

async function request(payload) {
  const socket = net.createConnection(SOCKET);
  const frames = [];
  let buffer = '';
  await new Promise((resolve, reject) => {
    socket.once('connect', () => socket.write(`${JSON.stringify({ ...payload, token: TOKEN })}\n`));
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      let index;
      while ((index = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, index).trim();
        buffer = buffer.slice(index + 1);
        if (line) frames.push(JSON.parse(line));
      }
    });
    socket.on('close', resolve);
    socket.on('error', reject);
  });
  return frames;
}

async function registerRun() {
  for (const dir of [RUN_A, HOME_A, path.join(HOME_A, '.cache'),
    path.join(HOME_A, '.config'), path.join(HOME_A, '.local', 'share')])
    fs.mkdirSync(dir, { recursive: true });
  for (const [op, payload] of [
    ['register-workspace', { workspace: RUN_A }],
    ['register-home', { workspace: RUN_A, home: HOME_A }],
  ]) {
    const frames = await request({ op, ...payload });
    if (!frames.at(-1)?.ok) throw new Error(`${op} failed`);
  }
}

async function exec(commandLine, { timeoutMs = 600000, env = {}, network = true } = {}) {
  const frames = await request({
    op: 'exec',
    commandLine,
    workingDirectory: RUN_A,
    timeoutMs,
    networkEnabled: network,
    environment: env,
    readWritePaths: [RUN_A],
    readOnlyPaths: [],
  });
  const result = frames.find((f) => f.type === 'result') || frames.at(-1) || {};
  return {
    exitCode: result.exitCode,
    stdout: (result.stdout || '').trim(),
    stderr: (result.stderr || '').trim(),
    message: result.message,
  };
}

function show(label, r) {
  console.log(`--- ${label}`);
  console.log(`exit=${r.exitCode}${r.message ? ` message=${r.message}` : ''}`);
  if (r.stdout) console.log(r.stdout);
  if (r.stderr) console.log(`[stderr] ${r.stderr}`);
}

async function main() {
  await registerRun();

  if (CASE === 'npm') {
    show('npm install (real registry, no lockfile)', await exec(
      'set -e; rm -rf npmprobe; mkdir npmprobe; cd npmprobe; npm init -y >/dev/null; '
      + 'npm install --no-audit --no-fund left-pad@1.3.0 2>&1 | tail -5; '
      + 'node -e "console.log(\'left-pad says\', require(\'left-pad\')(\'x\',3,\'0\'))"'));
  }

  if (CASE === 'nuget') {
    show('dotnet restore with a real NuGet package', await exec(
      'set -e; rm -rf nugetprobe; mkdir nugetprobe; cd nugetprobe; '
      + 'dotnet new console --no-restore >/dev/null; '
      + 'dotnet add package Newtonsoft.Json --version 13.0.3 2>&1 | tail -3; '
      + 'dotnet restore 2>&1 | tail -3; '
      + 'printf \'%s\\n\' \'System.Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(new{ok=true}));\' > Program.cs; '
      + 'dotnet run --no-restore 2>&1 | tail -2'));
  }

  if (CASE === 'apt') {
    // `pv` is deliberately chosen because the image does not ship it: installing something that is
    // already present would prove nothing, and would make the cross-run isolation check below pass
    // for the wrong reason.
    show('the package is absent before the install', await exec(
      'command -v pv && echo "ALREADY-PRESENT (probe is invalid)" || echo "absent before install"'));
    show('apt-get install pv and cowsay into the per-run writable system root', await exec(
      'set -e; id -u; echo "uid_map:$(cat /proc/self/uid_map)"; '
      + 'apt-get update 2>&1 | tail -2; '
      + 'apt-get install -y --no-install-recommends pv cowsay 2>&1 | grep -Ev "Reading database" | tail -5; '
      + 'pv --version | head -1; /usr/games/cowsay -f tux "installed inside the run" | head -3; '
      + 'command -v pv && echo "INSTALLED"'));
    show('the installed package survives a second exec of the SAME run', await exec(
      'command -v pv >/dev/null && echo "PERSISTED-WITHIN-RUN" || echo "GONE"'));
    show('apt sources and reachability', await exec(
      'grep -rhoE "https?://[a-z0-9.-]+" /etc/apt/sources.list /etc/apt/sources.list.d/ 2>/dev/null | sort -u; '
      + 'curl -sS -o /dev/null -w "http80=%{http_code}\\n" --max-time 8 http://archive.ubuntu.com/ubuntu/ || echo "http80 failed"; '
      + 'curl -sS -o /dev/null -w "https443=%{http_code}\\n" --max-time 8 https://azure.archive.ubuntu.com/ubuntu/ || echo "https443 failed"'));
    show('writability of system dirs from inside the sandbox', await exec(
      'for d in /usr /usr/bin /var/lib/dpkg /etc; do printf "%s: " "$d"; (touch "$d/.probe" 2>/dev/null && echo writable && rm -f "$d/.probe") || echo read-only; done'));
  }

  // The installed package must be invisible to every other run and to the AgentHost container: the
  // writable system root is an overlay on a tmpfs inside ONE run's user namespace, so nothing it
  // contains may leak sideways. Run this after `apt`, reusing the same pod.
  if (CASE === 'apt-isolation') {
    const other = path.join(RUN_ROOT, 'run-b');
    const otherHome = path.join(RUN_ROOT, 'home-b');
    for (const dir of [other, otherHome, path.join(otherHome, '.cache'),
      path.join(otherHome, '.config'), path.join(otherHome, '.local', 'share')])
      fs.mkdirSync(dir, { recursive: true });
    for (const [op, payload] of [
      ['register-workspace', { workspace: other }],
      ['register-home', { workspace: other, home: otherHome }],
    ]) {
      const frames = await request({ op, ...payload });
      if (!frames.at(-1)?.ok) throw new Error(`${op} failed for run-b`);
    }

    const frames = await request({
      op: 'exec',
      commandLine: 'command -v pv && echo "LEAKED-TO-OTHER-RUN" || echo "ABSENT-IN-OTHER-RUN"',
      workingDirectory: other,
      timeoutMs: 120000,
      networkEnabled: false,
      environment: {},
      readWritePaths: [other],
      readOnlyPaths: [],
    });
    const result = frames.find((f) => f.type === 'result') || frames.at(-1) || {};
    show('a DIFFERENT run cannot see the first run\'s installed package', result);

    show('the AgentHost container itself never sees it', {
      exitCode: 0,
      stdout: fs.existsSync('/usr/bin/pv')
        ? 'LEAKED-TO-AGENTHOST (pv exists in the AgentHost container)'
        : 'ABSENT-IN-AGENTHOST (pv does not exist in the AgentHost container)',
    });
  }

  // Negative controls. Every one of these MUST fail; a success is a boundary violation.
  if (CASE === 'negative') {
    show('no host/node filesystem access', await exec(
      'for p in /host /proc/1/root/etc/hostname /var/lib/kubelet /etc/kubernetes /run/containerd; do '
      + 'printf "%s: " "$p"; (ls -d "$p" >/dev/null 2>&1 && echo "VISIBLE (violation)") || echo "absent"; done'));
    // Spelled without the literal secret path words the output redactor rewrites, so the verdict
    // itself survives redaction and a reviewer can read the result.
    show('no service-account token or kubelet credentials', await exec(
      'sa=/var/run/secrets/kubernetes.io/serviceaccount; '
      + 'printf "serviceaccount dir listing: "; (ls -A "$sa" 2>/dev/null | tr "\\n" " "; echo) || echo "absent"; '
      + 'printf "serviceaccount credential verdict: "; '
      + '(test -r "$sa/token" && echo "READABLE-VIOLATION") || echo "NOT-READABLE-OK"; '
      + 'printf "kubelet config verdict: "; '
      + '(test -r /var/lib/kubelet/config.yaml && echo "READABLE-VIOLATION") || echo "NOT-READABLE-OK"; '
      + 'printf "imds verdict: "; (curl -sS --max-time 5 -H Metadata:true '
      + '"http://169.254.169.254/metadata/instance?api-version=2021-02-01" >/dev/null 2>&1 '
      + '&& echo "REACHABLE-VIOLATION") || echo "UNREACHABLE-OK"'));
    // Each path is probed by a separate exec: the command validator rejects a whole command line
    // that merely mentions a cross-run path, which would otherwise hide the remaining results.
    // Ancestor directories are asserted on *contents*, not existence — bubblewrap has to
    // materialise the parent directories of a bind target, so the run legitimately sees the chain
    // leading to its own workspace. What must never appear there is another run's directory, so
    // each ancestor carries an allowlist of the run's own entries and anything else is a violation.
    const targets = [
      ['/workspace', []],
      ['/local-workspace', [path.basename(RUN_ROOT)]],
      [path.join(RUN_ROOT, 'run-b'), []],
      [RUN_ROOT, [path.basename(RUN_A), path.basename(HOME_A)]],
    ];
    for (const [target, allowed] of targets) {
      const frames = await request({
        op: 'exec',
        commandLine: 'ls -d "$AWX_PROBE_TARGET" >/dev/null 2>&1 || { echo "ABSENT-OK"; exit 0; }; '
          + 'unexpected=$(ls -A "$AWX_PROBE_TARGET" 2>/dev/null '
          + '| grep -vxF "$(printf %b "$AWX_PROBE_ALLOWED")" || true); '
          + 'if [ -z "$unexpected" ]; then echo "ONLY-OWN-ENTRIES-OK"; '
          + 'ls -A "$AWX_PROBE_TARGET" 2>/dev/null | sed "s/^/  own: /"; '
          + 'else echo "FOREIGN-CONTENT-VIOLATION:"; echo "$unexpected"; fi',
        workingDirectory: RUN_A,
        timeoutMs: 60000,
        networkEnabled: false,
        environment: {
          AWX_PROBE_TARGET: target,
          // `grep -vxF` with a multi-line pattern removes each allowed entry; an empty allowlist
          // must not match anything, so it is a byte that cannot appear in a directory entry.
          AWX_PROBE_ALLOWED: allowed.length > 0 ? allowed.join('\\n') : '\\0',
        },
        readWritePaths: [RUN_A],
        readOnlyPaths: [],
      });
      const result = frames.find((f) => f.type === 'result') || frames.at(-1) || {};
      show(`no cross-run access to ${target}`, result);
    }
    show('no privileged escalation inside the sandbox', await exec(
      'printf "CapEff: "; grep ^CapEff /proc/self/status; '
      + 'printf "NoNewPrivs: "; grep ^NoNewPrivs /proc/self/status; '
      + 'printf "sudo: "; (sudo -n true 2>&1 && echo "SUCCEEDED (violation)") || echo "denied"; '
      + 'printf "mount /: "; (mount -o remount,rw / 2>&1 && echo "SUCCEEDED (violation)") || echo "denied"; '
      + 'printf "mknod: "; (mknod /tmp/probe-dev b 7 0 2>&1 && echo "SUCCEEDED (violation)") || echo "denied"'));
    show('the writable system root is not the image', await exec(
      'printf "/usr fstype: "; stat -f -c %T /usr; '
      + 'printf "/usr mount: "; grep -E " /usr " /proc/self/mountinfo | head -2'));
  }

  if (CASE === 'nested-userns') {
    show('nested user namespace + tooling availability', await exec(
      'echo "max_user_namespaces: $(cat /proc/sys/user/max_user_namespaces 2>/dev/null || echo unknown)"; '
      + 'echo "userns_clone: $(cat /proc/sys/kernel/unprivileged_userns_clone 2>/dev/null || echo n/a)"; '
      + 'printf "nested unshare -U: "; (unshare -U -r true 2>&1 && echo ok) || true; '
      + 'printf "nested unshare -U -m: "; (unshare -U -r -m true 2>&1 && echo ok) || true; '
      + 'printf "overlayfs in userns: "; '
      + 'mkdir -p ovl/l ovl/u ovl/w ovl/m; '
      + '(unshare -U -r -m sh -c "mount -t overlay overlay -o lowerdir=$PWD/ovl/l,upperdir=$PWD/ovl/u,workdir=$PWD/ovl/w $PWD/ovl/m" 2>&1 && echo ok) || true; '
      + 'for b in buildctl buildkitd rootlesskit newuidmap docker podman; do printf "%s: " "$b"; command -v $b || echo MISSING; done'));
  }

  if (CASE === 'buildkit') {
    show('buildkit rootless build of a fixture image', await exec(
      'set -x; rm -rf bkprobe; mkdir bkprobe; cd bkprobe; '
      + 'printf "FROM mcr.microsoft.com/cbl-mariner/busybox:2.0\\nRUN echo agentweaver-fixture > /fixture.txt\\nCMD [\\"cat\\",\\"/fixture.txt\\"]\\n" > Dockerfile; '
      + 'buildctl-daemonless.sh build --frontend dockerfile.v0 --local context=. --local dockerfile=. '
      + '--output type=oci,dest=fixture.tar 2>&1 | tail -20; ls -l fixture.tar'));
  }

  if (CASE === 'preview') {
    show('bind a preview port inside the sandbox and reach it from AgentHost', await exec(
      'set -e; (node -e "require(\'http\').createServer((_,res)=>res.end(\'preview-ok\')).listen(3000,\'127.0.0.1\')" & '
      + 'sleep 2; curl -sS --max-time 5 http://127.0.0.1:3000/ ; echo; '
      + 'ss -ltnp 2>/dev/null | head -5)'));
  }

  // The capability contract itself, straight off the executor protocol. This is what makes an
  // unsupported operation (winget) an explicit, machine-readable answer instead of a silent
  // omission that a caller only discovers by failing.
  if (CASE === 'capabilities') {
    const frames = await request({ op: 'capabilities' });
    const frame = frames.find((f) => f.type === 'capabilities') || frames.at(-1) || {};
    console.log('--- executor capability contract');
    console.log(`backend=${frame.message}`);
    for (const capability of frame.capabilities || []) {
      console.log(`${capability.id}: ${capability.state}`);
      console.log(`  detail: ${capability.detail}`);
      if (capability.remediation) console.log(`  remediation: ${capability.remediation}`);
    }
    if (!(frame.capabilities || []).some((c) => c.id === 'winget_install'))
      throw new Error('the contract must declare winget explicitly, never omit it');
  }
}

main().catch((error) => {
  console.error(`capability probe failed on ${os.hostname()}: ${error.message}`);
  process.exit(1);
});
