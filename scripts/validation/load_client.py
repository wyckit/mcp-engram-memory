"""Concurrent stdio MCP validation client. One response dispatcher, one queue per request."""
from collections import deque
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


class LoadClient:
    def __init__(self, command, environment, output, name, timeout=30):
        self.lock, self.pending = threading.Lock(), {}
        self.sequence, self.failure, self.timeout = 0, None, timeout
        self.closed = False
        self.recent, self.count = deque(maxlen=100), 0
        self.outbound = queue.Queue(maxsize=256)
        self.output, self.name = Path(output), name
        self.stderr = (self.output / (name + '.stderr.log')).open('w', encoding='utf-8')
        env = {k: v for k, v in os.environ.items() if not k.startswith(('MEMORY_', 'SYNTHESIS_')) and k != 'AGENT_ID'}
        env.update(environment)
        self.process = subprocess.Popen(command, env=env, cwd=self.output, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.stderr, text=True, encoding='utf-8',
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.writer = threading.Thread(target=self._write, daemon=True)
        self.reader.start()
        self.writer.start()
        try:
            self.request('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                'clientInfo': {'name': 'engram-soak', 'version': '1'}})
            self.notify('notifications/initialized')
        except BaseException:
            self.close(check=False)
            raise

    def _fail(self, error):
        with self.lock:
            self.failure = error
            for channel in self.pending.values(): channel.put(error)

    def _read(self):
        try:
            for line in self.process.stdout:
                response = json.loads(line)
                if response.get('jsonrpc') != '2.0': raise RuntimeError('Invalid MCP stdout: ' + line)
                if 'id' not in response: continue
                with self.lock:
                    channel = self.pending.get(response['id'])
                if channel is None: raise RuntimeError('Unmatched MCP response: ' + line)
                channel.put(response)
            self._fail(RuntimeError('MCP process ended'))
        except BaseException as error:
            self._fail(error)

    def _write(self):
        try:
            while True:
                message = self.outbound.get()
                if message is None:
                    self.process.stdin.close()
                    return
                self.process.stdin.write(json.dumps(message) + '\n')
                self.process.stdin.flush()
        except BaseException as error:
            self._fail(error)

    def notify(self, method):
        self.outbound.put({'jsonrpc': '2.0', 'method': method}, timeout=self.timeout)

    def request(self, method, params=None):
        channel = queue.Queue()
        started = time.monotonic()
        with self.lock:
            if self.failure: raise self.failure
            self.sequence += 1
            identity = self.sequence
            self.pending[identity] = channel
        try:
            self.outbound.put({'jsonrpc': '2.0', 'id': identity, 'method': method, 'params': params or {}}, timeout=self.timeout)
            try: response = channel.get(timeout=max(0, self.timeout - (time.monotonic() - started)))
            except queue.Empty: raise TimeoutError(f'{method}/{(params or {}).get("name", "")} exceeded {self.timeout}s')
            if isinstance(response, BaseException): raise response
            if 'error' in response: raise RuntimeError(response)
            return response['result']
        finally:
            with self.lock:
                self.pending.pop(identity, None)
                self.count += 1
                self.recent.append({'id': identity, 'method': method, 'params': params,
                                    'seconds': time.monotonic() - started})

    def call(self, tool, **arguments):
        result = self.request('tools/call', {'name': tool, 'arguments': arguments})
        if result.get('isError'): raise RuntimeError(result)
        text = '\n'.join(c['text'] for c in result.get('content', []) if c['type'] == 'text')
        try: return json.loads(text)
        except ValueError: return text

    def close(self, check=True):
        if self.closed: return
        self.closed = True
        forced = False
        try:
            self.outbound.put_nowait(None)
        except queue.Full:
            forced = True
            self.process.kill()
        try: self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            forced = True
            self.process.kill()
            self.process.wait(timeout=10)
        self.reader.join(timeout=5)
        self.writer.join(timeout=5)
        try: self.process.stdin.close()
        except (OSError, ValueError): pass
        self.process.stdout.close()
        self.stderr.close()
        if check and (forced or self.process.returncode): raise RuntimeError(f'Unclean shutdown: {self.name}')

    def __enter__(self): return self
    def __exit__(self, error, *_): self.close(check=error is None)
