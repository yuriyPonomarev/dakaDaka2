"""Local-verifier regression tests for the observed WinError5/EPIPE failure."""
import ctypes
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import status_writer


class StatusWriterTests(unittest.TestCase):
    def test_transient_replace_failure_retries_without_destroying_previous_json(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            target.write_text('{"state":"previous"}', encoding="utf-8")
            real_replace = os.replace
            attempts = []

            def sharing_then_success(source, destination):
                attempts.append(1)
                if len(attempts) < 3:
                    self.assertEqual(json.loads(target.read_text())["state"], "previous")
                    raise PermissionError(13, "simulated Windows sharing violation")
                return real_replace(source, destination)

            with patch.object(status_writer.os, "replace", side_effect=sharing_then_success), patch.object(status_writer.time, "sleep"):
                self.assertTrue(status_writer.write_status(target, {"state": "running", "job": "проверка"}))
            self.assertEqual(len(attempts), 3)
            self.assertEqual(json.loads(target.read_text(encoding="utf-8")), {"state": "running", "job": "проверка"})
            self.assertEqual(list(Path(directory).glob("*.tmp")), [])

    def test_persistent_failure_leaves_last_status_and_recovers_next_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            target.write_text('{"state":"previous"}', encoding="utf-8")
            with patch.object(status_writer.os, "replace", side_effect=PermissionError(13, "busy")), patch.object(status_writer.time, "sleep"), patch.object(status_writer.sys, "stderr", io.StringIO()):
                self.assertFalse(status_writer.write_status(target, {"state": "running"}))
            self.assertEqual(json.loads(target.read_text()), {"state": "previous"})
            self.assertEqual(list(Path(directory).glob("*.tmp")), [])
            self.assertTrue(status_writer.write_status(target, {"state": "idle"}))
            self.assertEqual(json.loads(target.read_text()), {"state": "idle"})

    def test_temporary_file_write_failure_is_nonfatal(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            target.write_text('{"state":"previous"}', encoding="utf-8")
            with patch.object(status_writer.Path, "write_text", side_effect=PermissionError(13, "busy")), patch.object(status_writer.time, "sleep"), patch.object(status_writer.sys, "stderr", io.StringIO()):
                self.assertFalse(status_writer.write_status(target, {"state": "running"}))
            self.assertEqual(json.loads(target.read_text()), {"state": "previous"})

    @unittest.skipUnless(os.name == "nt", "regression needs Windows sharing semantics")
    def test_windows_reader_lock_does_not_break_worker_stdout_and_recovers(self):
        # Real Win32 reader permits read/write but denies delete/rename, reproducing WinError5.
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                      ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        kernel.CreateFileW.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel.CloseHandle.restype = ctypes.c_int
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            target.write_text('{"state":"previous"}', encoding="utf-8")
            handle = kernel.CreateFileW(str(target), 0x80000000, 0x1 | 0x2, None, 3, 0x80, None)
            self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
            worker = None
            observed = []
            try:
                # Output exceeds a pipe buffer; a reader crash would strand/break the child.
                code = "import json; [print(json.dumps({'event':i,'payload':'x'*4096}),flush=True) for i in range(32)]"
                worker = subprocess.Popen([sys.executable, "-u", "-c", code], stdin=subprocess.DEVNULL,
                                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                with patch.object(status_writer.sys, "stderr", io.StringIO()):
                    for line in worker.stdout:
                        event = json.loads(line)
                        observed.append(event["event"])
                        self.assertFalse(status_writer.write_status(target, {"state": "running", "event": event["event"]}))
                self.assertEqual(worker.wait(timeout=10), 0)
                self.assertEqual(worker.stderr.read(), "")
                self.assertEqual(observed, list(range(32)))
                self.assertEqual(json.loads(target.read_text()), {"state": "previous"})
            finally:
                kernel.CloseHandle(handle)
                if worker is not None:
                    if worker.poll() is None:
                        worker.kill()
                        worker.wait(timeout=10)
                    worker.stdout.close()
                    worker.stderr.close()
            self.assertTrue(status_writer.write_status(target, {"state": "review_pending", "events": len(observed)}))
            self.assertEqual(json.loads(target.read_text()), {"state": "review_pending", "events": 32})


if __name__ == "__main__":
    unittest.main(verbosity=2)
