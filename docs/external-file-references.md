# External file references (development preview)

This optional mode records a path, size and modification timestamp instead of copying an existing file into managed attachment storage. Source files are never deleted when a reference is removed. The default remains managed uploads.

Enable explicitly with `EASYPDM_EXTERNAL_FILES=true`. Configure `EASYPDM_EXTERNAL_SOURCE` (logical source label), `EASYPDM_EXTERNAL_PROVIDER_URL` (trusted internal HTTP service), and `EASYPDM_EXTERNAL_PUBLIC_PATH` (same-origin directory, default `/cad/`). Never accept provider URLs from end users. The provider receives the current session cookie and therefore must be trusted to handle EasyPDM sessions. Disable redirects and use a private network or authenticated transport.

The provider adapter must implement POST `api/resolve` returning `name`, `size`, `mtime_ns`, and optional `sha256`; POST `api/search` returning `hits` with `name`, `path`, and `extension`; and GET `api/source?reference=UUID`. Before streaming a source, it must call `/api/nas-references/{id}/source` with the current session so EasyPDM can enforce project access. It must confine paths to a configured read-only source root and reject symlinks, traversal, special files and unsupported source scopes. A bare caller-supplied path must never authorize a download.

Preview adapters may handle `?source=PATH` on their landing page. Generate meshes on demand, bound converter resources and cache derived data by source fingerprint. A preview is not authoritative manufacturing geometry. Index coverage and freshness must be visible; a missing search hit does not establish file absence.

Legacy attachment substitution requires administrator access and matching SHA-256 hashes. Existing files are retained for rollback. Backups of EasyPDM hold reference metadata; they do not back up external originals. References describe mutable source files, not immutable released revision snapshots.

Set `EASYPDM_EXTERNAL_ORIGIN` to the exact HTTPS public origin when a private reverse proxy terminates TLS. This accepts only that configured origin rather than trusting arbitrary forwarded headers. Resolve requests have a five-second timeout and return controlled unavailable results for provider network, timeout or malformed metadata failures. Stored references retain their source label; changing the configured label marks older references unavailable and denies source downloads. Operators must also preserve the physical root associated with a label.

Enabled-mode tests cover project denial, concurrent idempotent linking and unlinking, zero managed files, source changes and missing files, provider failures, legacy hash mismatch/match and upload retention, source-label mismatch, origin/path checks, and state revalidation after slow provider IO. Link/unlink serialize against item changes. Migration startup explicitly releases its session advisory lock before returning the physical connection to the pool.

This remains a development preview: live NAS/provider acceptance, integrated geometry-preview checks, localization and immutable released-revision semantics remain required before upstream submission. Synthetic API/provider contract tests do not constitute live NAS or manufacturing acceptance.
