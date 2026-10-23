# Experimental recoverable release-copy proof

This standalone POSIX/Python experiment captures an explicit set of files into a content-addressed store. A published release identifier cannot be overwritten through the tool. Restore requires an independently retained manifest SHA-256, reads stored objects rather than live source paths, and refuses an existing destination. It does not change source files or assign item numbers.

It is **not WORM storage, a signed release approval, an automatic CAD dependency collector, an EasyPDM revision UI, or a scheduled/off-site backup**. A storage administrator can still modify the store. Keep the manifest pin in a separately protected operational record, and establish real backup/retention/restore policy before production use. External-reference source paths alone remain mutable.

## Try it on copies

Python3.10+ on Linux/macOS, standard library only. Do not point an experiment at production working files. The specification explicitly lists the complete copyset:

```json
{
  "item_id": "synthetic-native-proof",
  "external_code": null,
  "scope": "Synthetic native CAD only",
  "revision": "A",
  "files": [
    {
      "relative_path": "model.3dm",
      "source_path": "/isolated/working/model.3dm",
      "expected_sha256": "replace-with-the-verified-source-sha256",
      "provenance": "An isolated, verified native output"
    }
  ]
}
```

```sh
python3 tools/release_snapshot.py capture --store /isolated/store --release A --spec /isolated/spec-A.json
python3 tools/release_snapshot.py verify --store /isolated/store --release A
python3 tools/release_snapshot.py restore --store /isolated/store --release A --manifest-sha256 SAVED_MANIFEST_SHA256 --target /isolated/restored-A
python3 -m unittest discover -s tests/release_snapshot -p 'test_*.py'
```

Capture returns a `manifest_sha256`; retain that pin independently. The manifest records logical identity, optional existing external code, scope, revision, exact relative paths, sizes, hashes and supplied dependency evidence. Source fingerprints are checked before publication. Explicit limits are256files,256MiB per file and512MiB per release; these do not change any CAD worker's limits. Case-equivalent paths, traversal, symlink sources and reserved restore metadata names are rejected.

For an assembly, collect the exact closure through the native CAD API; a directory dump may add unrelated in-context references. Restore all dependencies and reopen with the actual CAD engine. Native tools may remap paths into a sandbox: retain the original package hashes separately from hashes of remapped inspection copies. Do not infer mates/transforms or complete-machine acceptance from a component-list-only receipt.

## Acceptance and remaining work

Five tests cover recovery of A after the working file becomes B, release/destination overwrite rejection, stored-object corruption, manifest-identity tampering against an external pin, and unsafe/colliding paths. A bounded native A/B experiment must additionally reopen the restored A and verify its prior geometry while the working file stays B. A separate assembly reconstruction must resolve the exact dependency set. Neither test establishes production-wide revision completeness or backup coverage.

No web endpoint, automatic release hook, retention schedule, migration or default application behavior is changed. Those require separate design and acceptance.

Independent review found no blocker for this trusted single-user POSIX experiment. Before production integration, add directory fsync around publication and restore completion for crash/power-loss durability, enforce the native destination's complete filename policy (including Windows device names and reserved characters), and implement app authorization and independently verified store backup coverage. Current tests do not establish hostile multi-user filesystem/race resistance or power-loss recovery.
