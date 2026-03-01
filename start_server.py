#!/usr/bin/env python3
"""Start MCP server and keep it alive. Ctrl+C to stop."""
import subprocess, sys, os, threading, time, signal

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
LAUNCHER = os.path.join(os.path.dirname(__file__), "bridge-launcher.mjs")

def drain(stream, prefix):
    for line in iter(stream.readline, ""):
        line = line.rstrip()
        if line:
            print(f"[{prefix}] {line}", flush=True)

proc = subprocess.Popen(
    ["node", LAUNCHER, "--verbose"],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1,
    cwd=REPO_ROOT,
    env={**os.environ, "BRIDGE_DEBUG": "1"},
)

threading.Thread(target=drain, args=(proc.stdout, "out"), daemon=True).start()
threading.Thread(target=drain, args=(proc.stderr, "err"), daemon=True).start()

print(f"MCP server started (PID {proc.pid}). Waiting...", flush=True)

try:
    while proc.poll() is None:
        time.sleep(1)
except KeyboardInterrupt:
    pass
finally:
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except:
        proc.kill()
    print("MCP server stopped.", flush=True)
