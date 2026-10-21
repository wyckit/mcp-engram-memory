import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
from concurrent.futures import ThreadPoolExecutor

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
from validation.load_client import LoadClient
spec = importlib.util.spec_from_file_location('soak', SCRIPTS / 'run-release-soak.py')
soak = importlib.util.module_from_spec(spec)
spec.loader.exec_module(soak)

SERVER = '''
import json,sys
waiting=[]
for line in sys.stdin:
 r=json.loads(line)
 if 'id' not in r: continue
 if r['method']=='initialize':
  print(json.dumps({'jsonrpc':'2.0','id':r['id'],'result':{}}),flush=True)
 elif r['method']=='bad':
  print('not JSON',flush=True)
 else:
  waiting.append(r)
  if len(waiting)==2:
   for item in reversed(waiting):
    print(json.dumps({'jsonrpc':'2.0','id':item['id'],'result':item['params']}),flush=True)
   waiting=[]
'''


class ClientTests(unittest.TestCase):
    def test_out_of_order_responses_go_to_their_request(self):
        with tempfile.TemporaryDirectory() as root:
            with LoadClient([sys.executable, '-u', '-c', SERVER], {}, root, 'reverse', timeout=2) as client:
                with ThreadPoolExecutor(max_workers=2) as pool:
                    futures = [pool.submit(client.request, 'echo', {'value': i}) for i in range(12)]
                    self.assertEqual([f.result() for f in futures], [{'value': i} for i in range(12)])

    def test_missing_response_has_a_deadline(self):
        with tempfile.TemporaryDirectory() as root:
            with LoadClient([sys.executable, '-u', '-c', SERVER], {}, root, 'timeout', timeout=5) as client:
                # Bound interpreter startup separately from the deliberately missing reply.
                client.timeout = .2
                with self.assertRaises(TimeoutError): client.request('echo', {'value': 1})

    def test_non_protocol_stdout_fails_request(self):
        with tempfile.TemporaryDirectory() as root:
            with LoadClient([sys.executable, '-u', '-c', SERVER], {}, root, 'invalid', timeout=2) as client:
                with self.assertRaises(ValueError): client.request('bad')

    def test_blocked_stdin_cannot_bypass_request_deadline(self):
        server = "import json,sys,time; r=json.loads(sys.stdin.readline()); print(json.dumps({'jsonrpc':'2.0','id':r['id'],'result':{}}),flush=True); time.sleep(30)"
        with tempfile.TemporaryDirectory() as root:
            client = LoadClient([sys.executable, '-u', '-c', server], {}, root, 'blocked-input', timeout=5)
            try:
                client.timeout = .2
                started = time.monotonic()
                with self.assertRaises(TimeoutError): client.request('echo', {'value': 'x' * 1024 * 1024})
                self.assertLess(time.monotonic() - started, 3)
            finally:
                client.process.kill()
                client.close(check=False)


class OracleTests(unittest.TestCase):
    def setUp(self):
        self.corpus = json.loads((SCRIPTS.parent / 'tests/McpEngramMemory.Tests/Fixtures/recall-independent-v1.json').read_text())

    def result(self, *ids):
        entries = {e['id']: e for e in self.corpus['entries']}
        return {'results': [{'id': i, 'text': entries[i]['text']} for i in ids]}

    def test_compound_facets_are_route_scoped_without_relabeling(self):
        question = next(q for q in self.corpus['queries'] if q['id'] == 'compound-recovery')
        soak.check_recall(self.corpus, question, 'direct', self.result('c-owner'))
        with self.assertRaises(AssertionError): soak.check_recall(self.corpus, question, 'unscoped', self.result('c-owner'))
        soak.check_recall(self.corpus, question, 'unscoped', self.result('c-owner', 'c-backup'))

    def test_wrong_scope_and_negative_evidence_fail(self):
        question = self.corpus['queries'][0]
        with self.assertRaises(AssertionError): soak.check_recall(self.corpus, question, 'direct', self.result('c-stage-owner'))
        question = next(q for q in self.corpus['queries'] if q['id'] == 'absent-date')
        with self.assertRaises(AssertionError): soak.check_recall(self.corpus, question, 'direct', self.result('c-date-note'))
        soak.check_recall(self.corpus, question, 'direct', self.result())

    def test_permission_canary_is_always_fatal_even_with_unrelated_evidence(self):
        question = self.corpus['queries'][0]
        result = {'results': [{'id': 'background-27', 'text': 'Library'},
                              {'id': 'hidden', 'text': soak.CANARIES[0]}]}
        with self.assertRaises(soak.ScopeViolation): soak.check_recall(self.corpus, question, 'unscoped', result)
        result = self.result('c-stage-owner')
        result['results'].insert(0, {'id': 'background-27', 'text': 'Library'})
        with self.assertRaises(soak.ScopeViolation): soak.check_recall(self.corpus, question, 'unscoped', result)


class StatusTests(unittest.TestCase):
    def test_temporary_windows_reader_lock_retries_atomic_replacement(self):
        with tempfile.TemporaryDirectory() as root:
            destination = Path(root) / 'status.json'
            destination.write_text('{"old": true}')
            replace = Path.replace
            attempts = []
            def locked_once(source, target):
                attempts.append(target)
                if len(attempts) == 1: raise PermissionError('Windows reader holds the destination')
                return replace(source, target)
            with patch.object(Path, 'replace', locked_once): soak.write_json(destination, {'new': True})
            self.assertEqual(json.loads(destination.read_text()), {'new': True})
            self.assertEqual(len(attempts), 2)

    def test_permanent_status_error_still_fails(self):
        with tempfile.TemporaryDirectory() as root:
            with patch.object(Path, 'replace', side_effect=PermissionError('permanent')) as replace:
                with patch.object(soak.time, 'sleep'):
                    with self.assertRaises(PermissionError): soak.write_json(Path(root) / 'status.json', {})
            self.assertEqual(replace.call_count, 20)


if __name__ == '__main__': unittest.main()
