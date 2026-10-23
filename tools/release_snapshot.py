#!/usr/bin/env python3
"""Explicit-file release snapshots. Write-once by tool contract, not WORM storage.

Originals are read only. Restore uses stored bytes, never live source paths.
This experiment does not implement an approval workflow or scheduled backup.
"""
import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import unicodedata


def relative(value):
    if not isinstance(value, str) or not value or value.startswith('/') or '\\' in value or ':' in value or any(ord(c) < 32 for c in value):
        raise ValueError('Invalid relative path')
    if any(p in ('', '.', '..') or p.endswith((' ', '.')) for p in value.split('/')):
        raise ValueError('Invalid relative path component')
    return value


def digest(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, 'rb') as source:
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
            raise ValueError('Regular files required')
        h = hashlib.sha256()
        size = 0
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            h.update(chunk)
            size += len(chunk)
        return h.hexdigest(), size


def exclusive(path, data, mode=0o400):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    with os.fdopen(fd, 'wb') as out:
        out.write(data)
        out.flush()
        os.fsync(out.fileno())


class ReleaseStore:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        for path in (self.root, self.root / 'objects', self.root / 'releases'):
            if path.is_symlink():
                raise ValueError('Store directories cannot be symlinks')
            path.mkdir(exist_ok=True, mode=0o700)

    @contextlib.contextmanager
    def lock(self):
        fd = os.open(self.root / '.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'r+b') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            yield

    def manifest_path(self, release):
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,100}', release):
            raise ValueError('Invalid release identifier')
        return self.root / 'releases' / (release + '.json')

    def capture(self, release, spec):
        manifest_path = self.manifest_path(release)
        files = spec['files']
        if not 1 <= len(files) <= 256:
            raise ValueError('Explicit file set must contain 1 to 256 files')
        names = [relative(f['relative_path']) for f in files]
        folded = [unicodedata.normalize('NFC', n).casefold() for n in names]
        if any(n in ('.release-manifest.json', '.restore-incomplete') for n in folded):
            raise ValueError('Reserved restore metadata name')
        if len(set(folded)) != len(names) or any(a.startswith(b + '/') for a in folded for b in folded if a != b):
            raise ValueError('Colliding file paths')
        if not spec.get('item_id') or not spec.get('scope'):
            raise ValueError('Logical identity and scope required')
        with self.lock():
            if manifest_path.exists() or manifest_path.is_symlink():
                raise FileExistsError('Release is already published; never overwrite')
            rows = []
            total = 0
            for entry, name in zip(files, names):
                source = Path(entry['source_path'])
                fd = os.open(source, os.O_RDONLY | os.O_NOFOLLOW)
                temp = None
                try:
                    with os.fdopen(fd, 'rb') as stream:
                        before = os.fstat(stream.fileno())
                        if not stat.S_ISREG(before.st_mode) or before.st_size > 256 * 1024**2:
                            raise ValueError('Source type or size outside bounds')
                        h = hashlib.sha256()
                        size = 0
                        tmpfd, temp = tempfile.mkstemp(prefix='.capture-', dir=self.root)
                        with os.fdopen(tmpfd, 'wb') as out:
                            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                                size += len(chunk)
                                if size > 256 * 1024**2:
                                    raise ValueError('Source grew beyond bounds')
                                h.update(chunk)
                                out.write(chunk)
                            out.flush()
                            os.fsync(out.fileno())
                        sha = h.hexdigest()
                        after = os.fstat(stream.fileno())
                        now = os.stat(source, follow_symlinks=False)
                        if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (now.st_dev, now.st_ino, now.st_size, now.st_mtime_ns) or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns) or digest(source) != (sha, size):
                            raise ValueError('Source changed during capture')
                    total += size
                    if total > 512 * 1024**2:
                        raise ValueError('Release exceeds 512 MiB')
                    if entry.get('expected_sha256') and entry['expected_sha256'] != sha:
                        raise ValueError('Expected source hash mismatch')
                    blob = self.root / 'objects' / sha
                    try:
                        os.chmod(temp, 0o400)
                        os.link(temp, blob, follow_symlinks=False)
                    except FileExistsError:
                        if digest(blob) != (sha, size):
                            raise ValueError('Existing object is corrupt')
                    rows.append({'relative_path': name, 'sha256': sha, 'bytes': size, 'provenance': entry.get('provenance')})
                finally:
                    if temp:
                        Path(temp).unlink(missing_ok=True)
            manifest = {'schema': 'release-copy-proof.v1', 'release_id': release,
                        'item_id': spec['item_id'], 'external_code': spec.get('external_code'),
                        'scope': spec['scope'], 'revision': spec.get('revision'),
                        'created_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        'protection': 'write-once by tool contract; not WORM or a backup schedule',
                        'files': rows, 'dependency_evidence': spec.get('dependency_evidence')}
            exclusive(manifest_path, json.dumps(manifest, ensure_ascii=False, sort_keys=True, indent=2).encode())
            return self.verify(release)

    def verify(self, release, expected_manifest_sha256=None):
        path = self.manifest_path(release)
        if path.is_symlink():
            raise ValueError('Symlink manifest denied')
        manifest_sha, manifest_size = digest(path)
        if manifest_size > 1024 * 1024 or (expected_manifest_sha256 is not None and manifest_sha != expected_manifest_sha256):
            raise ValueError('Pinned manifest hash mismatch or oversized manifest')
        manifest = json.loads(path.read_text())
        if manifest.get('schema') != 'release-copy-proof.v1' or manifest['release_id'] != release:
            raise ValueError('Manifest identity mismatch')
        for entry in manifest['files']:
            relative(entry['relative_path'])
            sha = entry['sha256']
            if not re.fullmatch(r'[a-f0-9]{64}', sha) or digest(self.root / 'objects' / sha) != (sha, entry['bytes']):
                raise ValueError('Stored release integrity failed')
        return {**manifest, 'manifest_sha256': manifest_sha}

    def restore(self, release, target, expected_manifest_sha256):
        with self.lock():
            if not re.fullmatch(r'[a-f0-9]{64}', expected_manifest_sha256):
                raise ValueError('Pinned manifest hash required')
            manifest = self.verify(release, expected_manifest_sha256)
            target = Path(target)
            target.mkdir(mode=0o700, parents=False, exist_ok=False)
            marker = target / '.restore-incomplete'
            exclusive(marker, b'Incomplete until every stored byte is verified', 0o600)
            for entry in manifest['files']:
                out = target / relative(entry['relative_path'])
                out.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                source = self.root / 'objects' / entry['sha256']
                exclusive(out, source.read_bytes(), 0o600)
                if digest(out) != (entry['sha256'], entry['bytes']):
                    raise ValueError('Restored bytes differ')
            exclusive(target / '.release-manifest.json', json.dumps(manifest, ensure_ascii=False, sort_keys=True, indent=2).encode(), 0o600)
            marker.unlink()
            return {'release_id': release, 'item_id': manifest['item_id'], 'external_code': manifest['external_code'], 'restored_files': len(manifest['files']), 'all_hashes_equal': True, 'source_paths_used': False}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('operation', choices=['capture', 'verify', 'restore'])
    p.add_argument('--store', required=True)
    p.add_argument('--release', required=True)
    p.add_argument('--spec')
    p.add_argument('--target')
    p.add_argument('--manifest-sha256')
    args = p.parse_args()
    store = ReleaseStore(args.store)
    if args.operation == 'capture':
        result = store.capture(args.release, json.loads(Path(args.spec).read_text()))
    elif args.operation == 'restore':
        result = store.restore(args.release, args.target, args.manifest_sha256 or '')
    else:
        result = store.verify(args.release, args.manifest_sha256)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
