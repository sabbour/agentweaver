#!/usr/bin/env node
// In-pod evidence client for the AgentHost executor sidecar (#476 / Kata corrective).
//
// Runs INSIDE the AgentHost container of a sandbox pod and speaks the real executor
// protocol (NDJSON over the pod-private unix socket, see
// packages/Agentweaver.SandboxExec/PodExec/PodExecProtocol.cs). It deliberately uses no
// test hooks: every result below is what a production AgentHost would get.
//
// Usage (inside the container):
//   node kata-sidecar-probe.mjs <case> [runRoot]
//
// Cases: probe | coreclr | process | mount | procfs | argv | stdout | termination
//
// Secrets are never printed: the executor token is read from disk and only its length is
// reported.

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const SOCKET = process.env.AGENTWEAVER_EXEC_SOCKET || '/var/run/agentweaver-exec/exec.sock';
const TOKEN_PATH = path.join(path.dirname(SOCKET), 'exec.token');
const TOKEN = fs.readFileSync(TOKEN_PATH, 'utf8').trim();
const CASE = process.argv[2] || 'probe';
const RUN_ROOT = process.argv[3] || '/local-workspace/evidence';
const RUN_A = path.join(RUN_ROOT, 'run-a');
const RUN_B = path.join(RUN_ROOT, 'run-b');
const HOME_A = path.join(RUN_ROOT, 'home-a');

function connect() {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(SOCKET);
    socket.once('connect', () => resolve(socket));
    socket.once('error', reject);
  });
}

// One request per connection; frames stream back until the socket closes.
async function request(payload, onFrame) {
  const socket = await connect();
  const frames = [];
  let buffer = '';
  socket.write(JSON.stringify({ ...payload, token: TOKEN }) + '\n');
  await new Promise((resolve, reject) => {
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      let index;
      while ((index = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, index).trim();
        buffer = buffer.slice(index + 1);
        if (!line) continue;
        const frame = JSON.parse(line);
        frames.push(frame);
        if (onFrame) onFrame(frame, socket);
      }
    });
    socket.on('close', resolve);
    socket.on('error', reject);
  });
  return frames;
}

// Opens a supervised spawn and returns once the workload is running, keeping the
// connection open (that connection IS the die-with-parent supervisor).
const callerPidNamespace = () => fs.readlinkSync('/proc/self/ns/pid');

// Opens a supervised spawn...
function spawnSupervised(commandLine, { workingDirectory = RUN_A, timeoutMs = 120000, readyMarker = null } = {}) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(SOCKET);
    const frames = [];
    let buffer = '';
    let settled = false;
    let processGroupId = 0;
    const settle = () => {
      if (settled) return;
      settled = true;
      resolve({ socket, frames, processGroupId: () => processGroupId });
    };
    socket.once('connect', () => {
      socket.write(`${JSON.stringify({
        op: 'spawn',
        token: TOKEN,
        commandLine,
        workingDirectory,
        timeoutMs,
        networkEnabled: false,
        readWritePaths: [RUN_A],
      })}\n`);
    });
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      let index;
      while ((index = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, index).trim();
        buffer = buffer.slice(index + 1);
        if (!line) continue;
        const frame = JSON.parse(line);
        frames.push(frame);
        if (frame.type === 'started') processGroupId = frame.processGroupId;
        if (frame.type === 'error') { settled = true; reject(new Error(frame.message || 'spawn failed')); return; }
        if (!readyMarker || (frame.data || '').includes(readyMarker)) settle();
      }
    });
    socket.on('error', reject);
    socket.on('close', settle);
    setTimeout(settle, 20000).unref?.();
  });
}

async function probe() {
  const frames = await request({ op: 'probe', callerPidNamespace: callerPidNamespace() });
  const frame = frames.find((f) => f.type === 'probe') || frames[0];
  console.log(`caller (AgentHost) pid ns : ${callerPidNamespace()}`);
  console.log(`token file                : ${TOKEN_PATH} (${TOKEN.length} chars, value withheld)`);
  console.log(`probe ok                  : ${frame.ok}`);
  console.log(`probe detail              : ${frame.detail}`);
  return frame.ok;
}

async function registerRun() {
  const dirs = [
    RUN_A,
    RUN_B,
    HOME_A,
    path.join(HOME_A, '.cache'),
    path.join(HOME_A, '.config'),
    path.join(HOME_A, '.local', 'share'),
  ];
  for (const dir of dirs) fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(RUN_B, 'sibling-secret.txt'), 'sibling run private data\n');
  for (const [op, payload] of [
    ['register-workspace', { workspace: RUN_A }],
    ['register-home', { workspace: RUN_A, home: HOME_A }],
  ]) {
    const frames = await request({ op, ...payload });
    const frame = frames[frames.length - 1] || {};
    if (!frame.ok) throw new Error(`${op} failed: ${frame.message || frame.detail || 'no ack frame'}`);
    console.log(`registered ${op}: ok`);
  }
}

async function exec(commandLine, { workingDirectory = RUN_A, timeoutMs = 180000, env = {} } = {}) {
  const frames = await request({
    op: 'exec',
    commandLine,
    workingDirectory,
    timeoutMs,
    networkEnabled: false,
    environment: env,
    readWritePaths: [RUN_A],
    readOnlyPaths: [],
  });
  const result = frames.find((f) => f.type === 'result') || frames[frames.length - 1] || {};
  return {
    exitCode: result.exitCode,
    stdout: (result.stdout || '').trim(),
    stderr: (result.stderr || '').trim(),
    message: result.message,
    type: result.type,
  };
}

function show(label, r) {
  console.log(`--- ${label}`);
  console.log(`exit=${r.exitCode}${r.type === 'error' ? ` type=error message=${r.message}` : ''}`);
  if (r.stdout) console.log(r.stdout);
  if (r.stderr) console.log(`[stderr] ${r.stderr}`);
}

async function main() {
  if (CASE === 'probe') {
    const ok = await probe();
    process.exit(ok ? 0 : 1);
  }

  await registerRun();

  if (CASE === 'coreclr') {
    show('dotnet new console + run (real CoreCLR inside the sandbox)', await exec(
      'set -e; rm -rf clrprobe; mkdir clrprobe; cd clrprobe; '
      + 'dotnet new console --no-restore >/dev/null; dotnet restore >/dev/null 2>&1; '
      + 'printf \'%s\\n\' \'System.Console.WriteLine($"clr-ok pid={System.Environment.ProcessId} '
      + 'fx={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} '
      + 'os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");\' > Program.cs; '
      + 'dotnet run --no-restore 2>&1 | tail -3'));
    show('node runtime', await exec('node --version'));
  }

  if (CASE === 'process') {
    show('processes visible to the sandbox (executor container only)', await exec(
      'ps -eo pid,ppid,comm 2>/dev/null | head -20'));
    show('sandbox pid namespace vs AgentHost pid namespace', await exec(
      `readlink /proc/self/ns/pid; echo "agenthost-ns-from-client: ${callerPidNamespace()}"`));
    // The sidecar runs the same image, so match the A2A server specifically: it is the only
    // AgentHost process WITHOUT --exec-agent. Expect 0 of those to be visible.
    show('AgentHost A2A server processes reachable from the sandbox (expect 0)', await exec(
      'ps -eo args 2>/dev/null | grep "Agentweaver.AgentHost.dll" | grep -v -- "--exec-agent" | wc -l; '
      + 'echo "--- every AgentHost process the sandbox can see:"; '
      + 'ps -eo args 2>/dev/null | grep "Agentweaver.AgentHost.dll" | grep -v grep'));
  }

  if (CASE === 'mount') {
    // (a) static policy: a literal shared-root path is refused before anything runs.
    show('literal /workspace reference (expect exit 126, command never runs)', await exec(
      'ls /workspace | head -1'));
    // (b) mount-level: build the same paths at runtime so the text policy cannot see them,
    //     proving the isolation does not depend on command text.
    show('runtime-constructed paths: sibling run, PVC root, exec socket, SA token', await exec(
      'w=$(printf "/%s%s" works pace); s=$(printf "%s" "' + SOCKET + '"); '
      + `b=$(printf "%s" "${RUN_B}/sibling-secret.txt"); `
      + 'ls "$b" 2>&1 | tail -1; ls "$w" 2>&1 | tail -1; ls "$s" 2>&1 | tail -1; '
      + 'ls /var/run/secrets/kubernetes.io/serviceaccount 2>&1 | tail -1; '
      + 'echo "AZURE_ env count: $(env | grep -c \'^AZURE_\' || true)"'));
    show('read-only system paths', await exec(
      'touch /usr/should-fail 2>&1 | tail -1; touch /etc/should-fail 2>&1 | tail -1; echo done'));
  }

  if (CASE === 'procfs') {
    show('masked procfs preserved inside bwrap', await exec(
      'for p in /proc/kcore /proc/kmsg /proc/sysrq-trigger /proc/sys/kernel/hostname; do '
      + 'printf "%s: " "$p"; (head -c 1 "$p" >/dev/null 2>&1 && echo readable) || echo denied; done; '
      + 'grep -E "^(CapEff|Uid)" /proc/self/status'));
  }

  if (CASE === 'argv') {
    // Documented residual: sibling commands of the SAME run share the executor container's
    // PID namespace, so argv is visible. Environment/root/mem must NOT be, because each
    // command gets its own user namespace.
    const peer = await spawnSupervised(
      'echo peer-ready; exec sleep 300',
      { readyMarker: 'peer-ready' });
    const peerPid = peer.processGroupId();
    show(`peer command pid/pgid ${peerPid}: argv visible, environ/root refused`, await exec(
      `p=${peerPid}; `
      + 'printf "peer cmdline: "; tr "\\0" " " < /proc/$p/cmdline 2>&1 | head -c 120; echo; '
      + 'printf "peer environ: "; (head -c 32 /proc/$p/environ >/dev/null 2>&1 && echo READABLE) || echo denied; '
      + 'printf "peer root:    "; (ls /proc/$p/root/ >/dev/null 2>&1 && echo READABLE) || echo denied; '
      + 'printf "sidecar pid 1 cmdline: "; tr "\\0" " " < /proc/1/cmdline 2>&1 | head -c 120; echo; '
      + 'printf "sidecar pid 1 environ: "; (head -c 32 /proc/1/environ >/dev/null 2>&1 && echo READABLE) || echo denied; '
      + 'printf "sidecar pid 1 root:    "; (ls /proc/1/root/ >/dev/null 2>&1 && echo READABLE) || echo denied; '
      + 'printf "exec socket via /proc/1/root: "; (ls /proc/1/root' + SOCKET + ' >/dev/null 2>&1 && echo READABLE) || echo denied'));
    peer.socket.destroy();
  }

  if (CASE === 'stdout') {
    const spawned = await spawnSupervised(
      'echo preview-listening; exec sleep 30',
      { readyMarker: 'preview-listening' });
    console.log('--- supervised spawn stdout (proves the workload keeps its own fd 1)');
    for (const frame of spawned.frames)
      console.log(`${frame.type}: ${(frame.data || '').trim() || `pgid=${frame.processGroupId}`}`);
    spawned.socket.destroy();
  }

  if (CASE === 'limits') {
    // NOTE: this case documents the *configured* limits and the backing store. It does not
    // claim that a runaway write is stopped by any particular one of them — the observed
    // exit 137 with no pod event is tracked in issue #758 and is deliberately unexplained.
    show('workspace backing store and container limits (configured, not claimed enforced)', await exec(
      'grep -E "local-workspace|/workspace " /proc/self/mountinfo | head -4; '
      + 'df -hT /local-workspace 2>/dev/null | tail -1; '
      + 'echo "memory.max: $(cat /sys/fs/cgroup/memory.max 2>/dev/null || echo unknown)"'));
  }

  if (CASE === 'termination') {
    const daemonPidFile = path.join(RUN_A, 'daemon.pid');
    try { fs.unlinkSync(daemonPidFile); } catch { /* first run */ }
    const supervised = await spawnSupervised(
      `sleep 600 & echo $! > ${daemonPidFile}; echo spawned; exec sleep 600`,
      { readyMarker: 'spawned' });
    const pgid = supervised.processGroupId();
    await new Promise((resolve) => { setTimeout(resolve, 1000); });
    const daemonPid = fs.existsSync(daemonPidFile) ? fs.readFileSync(daemonPidFile, 'utf8').trim() : '0';
    show(`before: pgid ${pgid} and daemonised child ${daemonPid} are alive`, await exec(
      `kill -0 -${pgid} 2>&1 | tail -1 && echo "group ${pgid}: alive"; `
      + `kill -0 ${daemonPid} 2>&1 | tail -1 && echo "daemon ${daemonPid}: alive"`));
    // Dropping the connection simulates the supervising relay (or AgentHost) dying.
    supervised.socket.destroy();
    await new Promise((resolve) => { setTimeout(resolve, 4000); });
    show(`after the supervisor dropped: group ${pgid} and daemon ${daemonPid} must be gone`, await exec(
      `(kill -0 -${pgid} 2>&1 || true) | tail -1; (kill -0 ${daemonPid} 2>&1 || true) | tail -1; `
      + 'echo "--- surviving processes in the executor container:"; ps -eo pid,comm 2>/dev/null | head -10'));
  }
}

main().catch((error) => {
  console.error(`evidence run failed on ${os.hostname()}: ${error.message}`);
  process.exit(1);
});
