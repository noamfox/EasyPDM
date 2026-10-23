import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

script = Path(__file__).parents[2] / 'tools/release_snapshot.py'
spec = importlib.util.spec_from_file_location('release_snapshot', script)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.working = self.root / 'working.3dm'
        self.working.write_bytes(b'native-revision-A')
        self.store = mod.ReleaseStore(self.root / 'store')
        self.spec = {'item_id': 'synthetic-native-item', 'scope': 'synthetic only', 'files': [{'relative_path': 'model.3dm', 'source_path': str(self.working)}]}

    def tearDown(self):
        self.temp.cleanup()

    def test_recovers_prior_bytes_after_live_file_is_changed(self):
        a = self.store.capture('A', self.spec)
        self.working.write_bytes(b'native-revision-B')
        b = self.store.capture('B', self.spec)
        restored = self.root / 'restored-A'
        receipt = self.store.restore('A', restored, a['manifest_sha256'])
        self.assertEqual((restored / 'model.3dm').read_bytes(), b'native-revision-A')
        self.assertEqual(self.working.read_bytes(), b'native-revision-B')
        self.assertEqual(a['item_id'], b['item_id'])
        self.assertFalse(receipt['source_paths_used'])

    def test_published_release_and_restore_destination_cannot_be_overwritten(self):
        a = self.store.capture('A', self.spec)
        with self.assertRaises(FileExistsError): self.store.capture('A', self.spec)
        target = self.root / 'existing'
        target.mkdir()
        marker = target / 'user-file'
        marker.write_bytes(b'keep')
        with self.assertRaises(FileExistsError): self.store.restore('A', target, a['manifest_sha256'])
        self.assertEqual(marker.read_bytes(), b'keep')

    def test_corrupt_stored_native_bytes_are_rejected_before_restore(self):
        a = self.store.capture('A', self.spec)
        blob = self.store.root / 'objects' / a['files'][0]['sha256']
        blob.chmod(0o600)
        blob.write_bytes(b'corruption')
        with self.assertRaises(ValueError): self.store.restore('A', self.root / 'restored', a['manifest_sha256'])
        self.assertFalse((self.root / 'restored').exists())

    def test_manifest_identity_tampering_rejected_by_external_pin(self):
        a = self.store.capture('A', self.spec)
        path = self.store.manifest_path('A')
        path.chmod(0o600)
        changed = json.loads(path.read_text())
        changed['item_id'] = 'different-item'
        path.write_text(json.dumps(changed))
        with self.assertRaises(ValueError): self.store.restore('A', self.root / 'restored', a['manifest_sha256'])

    def test_traversal_symlink_and_case_collisions_are_rejected(self):
        self.spec['files'][0]['relative_path'] = '../escape'
        with self.assertRaises(ValueError): self.store.capture('bad', self.spec)
        self.spec['files'][0]['relative_path'] = 'model.3dm'
        link = self.root / 'link.3dm'
        link.symlink_to(self.working)
        self.spec['files'][0]['source_path'] = str(link)
        with self.assertRaises(OSError): self.store.capture('bad-link', self.spec)
        self.spec['files'] = [{'relative_path': n, 'source_path': str(self.working)} for n in ['Part.SLDPRT', 'part.sldprt']]
        with self.assertRaises(ValueError): self.store.capture('bad-case', self.spec)


if __name__ == '__main__': unittest.main()
