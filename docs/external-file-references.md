# External file references (development preview)

This optional mode records a path, size and modification timestamp instead of copying an existing file into managed attachment storage. Source files are never deleted when a reference is removed. The default remains managed uploads.

Enable explicitly with `EASYPDM_EXTERNAL_FILES=true`. Configure `EASYPDM_EXTERNAL_SOURCE` (logical source label), `EASYPDM_EXTERNAL_PROVIDER_URL` (trusted internal HTTP service), and `EASYPDM_EXTERNAL_PUBLIC_PATH` (same-origin directory, default `/cad/`). Never accept provider URLs from end users. The provider receives the current session cookie and therefore must be trusted to handle EasyPDM sessions. Disable redirects and use a private network or authenticated transport.

The provider adapter must implement POST `api/resolve` returning `name`, `size`, `mtime_ns`, and optional `sha256`; POST `api/search` returning `hits` with `name`, `path`, and `extension`; and GET `api/source?reference=UUID`. Before streaming a source, it must call `/api/nas-references/{id}/source` with the current session so EasyPDM can enforce project access. It must confine paths to a configured read-only source root and reject symlinks, traversal, special files and unsupported source scopes. A bare caller-supplied path must never authorize a download.

Preview adapters may handle `?source=PATH` on their landing page. Generate meshes on demand, bound converter resources and cache derived data by source fingerprint. A preview is not authoritative manufacturing geometry. Index coverage and freshness must be visible; a missing search hit does not establish file absence.

Legacy attachment substitution requires administrator access and matching SHA-256 hashes. Existing files are retained for rollback. Backups of EasyPDM hold reference metadata; they do not back up external originals. References describe mutable source files, not immutable released revision snapshots.

This branch is a development preview. Provider conformance, enabled-mode authorization and concurrent mutation tests, UI localization, latest-main integration, and end-to-end pilot acceptance are required before upstream submission or deployment.
