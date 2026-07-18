// AKS scripts have PowerShell and bash counterparts; this provides one command from either shell.
import { existsSync } from 'node:fs';
import { resolve, basename, win32 } from 'node:path';
import { spawn } from 'node:child_process';

const defaultFolder = 'scripts/aks';

function printUsage() {
  console.error('Usage: node scripts/run-os-script.mjs <script-name> [folder] [-- <args...>]');
}

function run(command, args) {
  return new Promise((resolveExitCode, reject) => {
    const child = spawn(command, args, { stdio: 'inherit' });
    child.once('error', reject);
    child.once('close', (code, signal) => {
      resolveExitCode(code ?? (signal ? 1 : 0));
    });
  });
}

function toWslPath(filePath) {
  const normalized = win32.normalize(filePath);
  const driveMatch = /^([A-Za-z]):\\(.*)$/.exec(normalized);
  return driveMatch
    ? `/mnt/${driveMatch[1].toLowerCase()}/${driveMatch[2].replaceAll('\\', '/')}`
    : normalized.replaceAll('\\', '/');
}

async function runPowerShell(args) {
  try {
    return await run('pwsh', args);
  } catch (error) {
    if (error.code !== 'ENOENT') {
      throw error;
    }

    return run('powershell.exe', args);
  }
}

async function runBash(scriptPath, passthroughArgs) {
  const wslBash = 'C:\\Windows\\System32\\bash.exe';
  if (process.platform === 'win32' && existsSync(wslBash)) {
    return run(wslBash, [toWslPath(scriptPath), ...passthroughArgs]);
  }

  return run('bash', [scriptPath, ...passthroughArgs]);
}

const rawArgs = process.argv.slice(2);
const separatorIndex = rawArgs.indexOf('--');
const scriptArgs = separatorIndex === -1 ? rawArgs : rawArgs.slice(0, separatorIndex);
const passthroughArgs = separatorIndex === -1 ? [] : rawArgs.slice(separatorIndex + 1);
const [scriptName, folder = defaultFolder] = scriptArgs;

if (!scriptName || scriptArgs.length > 2 || basename(scriptName) !== scriptName) {
  printUsage();
  process.exitCode = 1;
} else {
  const scriptBasePath = resolve(folder, scriptName);

  try {
    if (process.platform === 'win32') {
      const powerShellScript = `${scriptBasePath}.ps1`;
      const bashScript = `${scriptBasePath}.sh`;
      if (existsSync(powerShellScript)) {
        process.exitCode = await runPowerShell([
          '-NoProfile',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          powerShellScript,
          ...passthroughArgs,
        ]);
      } else if (existsSync(bashScript)) {
        process.exitCode = await runBash(bashScript, passthroughArgs);
      } else {
        throw new Error(`Neither PowerShell nor bash script exists for '${scriptBasePath}'.`);
      }
    } else {
      const bashScript = `${scriptBasePath}.sh`;
      if (!existsSync(bashScript)) {
        throw new Error(`Bash script does not exist: '${bashScript}'.`);
      }

      process.exitCode = await runBash(bashScript, passthroughArgs);
    }
  } catch (error) {
    console.error(`Unable to run '${scriptName}': ${error.message}`);
    process.exitCode = 1;
  }
}
