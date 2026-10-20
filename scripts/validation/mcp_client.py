"""Small stdio client for isolated installed-package validation (no SDK client dependency)."""
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


class Client:
    def __init__(self, command, output, name, environment):
        self.output, self.name = Path(output), name
        self.messages, self.sequence, self.queue = [], 0, queue.Queue()
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(("MEMORY_", "SYNTHESIS_")) and k != "AGENT_ID"}
        env.update(environment)
        self.log = (self.output / f"{name}.stderr.log").open("w", encoding="utf-8")
        self.process = subprocess.Popen(command, cwd=output, env=env, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)

        def read():
            for line in self.process.stdout:
                try:
                    self.queue.put(json.loads(line))
                except ValueError:
                    self.queue.put({"invalid_stdout": line})
            self.queue.put({"process_ended": True})

        threading.Thread(target=read, daemon=True).start()
        try:
            self.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                "clientInfo": {"name": "engram-install-validation", "version": "1.0"}})
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except BaseException:
            self.close(check=False)
            raise

    def send(self, data):
        self.process.stdin.write(json.dumps(data) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.sequence += 1
        self.send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params or {}})
        deadline = time.monotonic() + 90
        while True:
            message = self.queue.get(timeout=max(.01, deadline - time.monotonic()))
            self.messages.append(message)
            if "invalid_stdout" in message or "process_ended" in message:
                raise RuntimeError(message)
            if message.get("id") == self.sequence:
                if "error" in message:
                    raise RuntimeError(message)
                return message["result"]

    def call(self, tool, **arguments):
        result = self.request("tools/call", {"name": tool, "arguments": arguments})
        if result.get("isError"):
            raise RuntimeError(result)
        text = "\n".join(x["text"] for x in result.get("content", []) if x["type"] == "text")
        try:
            return json.loads(text)
        except ValueError:
            return text

    def close(self, check=True):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        forced = False
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            forced = True
            self.process.kill()
            self.process.wait()
        self.log.close()
        (self.output / f"{self.name}.wire.json").write_text(json.dumps({"messages": self.messages,
            "exitCode": self.process.returncode, "forcedTermination": forced}, indent=2), encoding="utf-8")
        if check and (forced or self.process.returncode):
            raise RuntimeError(f"Server did not shut down cleanly: {self.name}")

    def __enter__(self):
        return self

    def __exit__(self, exception_type, *_):
        self.close(check=exception_type is None)
