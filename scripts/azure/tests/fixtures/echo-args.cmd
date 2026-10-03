@echo off
rem Test-only .cmd shim fixture: forwards every argument it receives to
rem node, which prints each argv entry on its own line. This mirrors how a
rem real shim (e.g. az.cmd) forwards %* to its underlying interpreter, so it
rem exercises the exact escaping/routing path in lib/exec.mjs's
rem spawnPlatformSafe, not a simplified stand-in.
node -e "process.argv.slice(1).forEach((a) => console.log(a))" %*
