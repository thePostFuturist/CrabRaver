#!/usr/bin/env python3
"""Interactive QA: start MCP server, wait for Unity, send commands."""
import subprocess, sys, os, json, threading, time

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
LAUNCHER = os.path.join(os.path.dirname(__file__), "bridge-launcher.mjs")

def drain_stderr(stream):
    for line in iter(stream.readline, ""):
        line = line.rstrip()
        if line:
            print(f"[err] {line}", file=sys.stderr, flush=True)

def send_and_receive(proc, request_id, method, params, timeout=60):
    """Send JSON-RPC request and wait for matching response."""
    msg = json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
    proc.stdin.write(msg + "\n")
    proc.stdin.flush()

    deadline = time.time() + timeout
    while time.time() < deadline:
        line = proc.stdout.readline().strip()
        if not line:
            time.sleep(0.1)
            continue
        try:
            resp = json.loads(line)
            if resp.get("id") == request_id:
                return resp
        except json.JSONDecodeError:
            pass
    return {"error": "timeout"}

def extract_text(resp):
    """Extract text from MCP SDK content wrapper."""
    content = resp.get("result", {}).get("content", [])
    if content and isinstance(content, list):
        text = content[0].get("text", "")
        try:
            return json.loads(text)
        except:
            return text
    return resp

def call_tool(proc, req_id, name, args=None, timeout=60):
    """Call an MCP tool and return parsed result."""
    resp = send_and_receive(proc, req_id, "tools/call",
                            {"name": name, "arguments": args or {}}, timeout=timeout)
    is_error = resp.get("result", {}).get("isError", False)
    parsed = extract_text(resp)
    return parsed, is_error

def main():
    commands = sys.argv[1:] if len(sys.argv) > 1 else ["full"]

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

    threading.Thread(target=drain_stderr, args=(proc.stderr,), daemon=True).start()
    print(f"MCP server started (PID {proc.pid}). Waiting for Unity to connect...", flush=True)

    # Wait for Unity to connect
    for attempt in range(120):
        time.sleep(1)
        result, _ = call_tool(proc, f"poll-{attempt}", "connection_status", timeout=3)
        if isinstance(result, dict) and result.get("connected"):
            print(f"Unity connected! (attempt {attempt+1})", flush=True)
            break
        if attempt % 10 == 9:
            print(f"  Still waiting... ({attempt+1}s)", flush=True)
    else:
        print("Timed out waiting for Unity.", flush=True)
        proc.terminate()
        return

    # Wait a moment for Bridge tools to load
    time.sleep(2)

    req_id = [0]
    def next_id():
        req_id[0] += 1
        return f"cmd-{req_id[0]}"

    def run_tool(label, name, args=None, timeout=60):
        print(f"\n{'='*50}", flush=True)
        print(f"  {label}", flush=True)
        print(f"  tool: {name}", flush=True)
        print(f"{'='*50}", flush=True)
        result, is_error = call_tool(proc, next_id(), name, args, timeout)
        if is_error:
            print(f"  ERROR: {result}", flush=True)
        elif isinstance(result, dict):
            print(json.dumps(result, indent=2), flush=True)
        else:
            print(f"  {result}", flush=True)
        return result, is_error

    for cmd in commands:
        if cmd == "full":
            # Full QA sequence
            run_tool("Connection Status", "connection_status")
            run_tool("Auth Status", "auth__get_status")
            run_tool("Player Position", "nav__get_position")
            run_tool("Load World: Party", "world__load_and_wait",
                     {"station": "Party"}, timeout=90)
            run_tool("World Status", "world__get_world_status")
            run_tool("Player Position (in world)", "nav__get_position")
            run_tool("Walk To [10, 0, 15]", "nav__walk_to",
                     {"destination": [10, 0, 15]})
            time.sleep(3)
            run_tool("Player Position (after walk)", "nav__get_position")
            run_tool("Screenshot", "vision__take_screenshot",
                     {"maxWidth": 512, "quality": 75})

        elif cmd == "status":
            run_tool("Connection Status", "connection_status")

        elif cmd == "auth":
            run_tool("Auth Status", "auth__get_status")

        elif cmd == "position":
            run_tool("Player Position", "nav__get_position")

        elif cmd == "tools":
            print("\n--- Tool List ---", flush=True)
            resp = send_and_receive(proc, next_id(), "tools/list", {}, timeout=10)
            tools = resp.get("result", {}).get("tools", [])
            print(f"Total tools: {len(tools)}", flush=True)
            for t in tools:
                print(f"  {t['name']}", flush=True)

        elif cmd == "load_world":
            run_tool("Load World: Party", "world__load_and_wait",
                     {"station": "Party"}, timeout=90)

        elif cmd == "walk":
            run_tool("Walk To [10, 0, 15]", "nav__walk_to",
                     {"destination": [10, 0, 15]})

        elif cmd == "screenshot":
            result, _ = run_tool("Screenshot", "vision__take_screenshot",
                     {"maxWidth": 512, "quality": 75})
            if isinstance(result, dict) and "data" in result:
                print(f"  (image data: {len(result['data'])} chars base64)", flush=True)

        elif cmd == "unload":
            run_tool("Unload World", "world__unload_and_wait", timeout=30)

        elif cmd == "interactive":
            print("\nInteractive mode. Press Ctrl+C to stop.", flush=True)
            try:
                while proc.poll() is None:
                    time.sleep(1)
            except KeyboardInterrupt:
                pass
            break

        else:
            print(f"Unknown command: {cmd}", flush=True)

    time.sleep(2)
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except:
        proc.kill()
    print("\nDone.", flush=True)

if __name__ == "__main__":
    main()
