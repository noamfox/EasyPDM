-- Additive metadata only. No source files or existing attachment rows are changed.
CREATE TABLE IF NOT EXISTS nas_file_references (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
 item_id UUID NOT NULL REFERENCES items(id) ON DELETE CASCADE,
 source TEXT NOT NULL DEFAULT 'archive',
 source_path TEXT NOT NULL,
 file_name TEXT NOT NULL,
 source_size BIGINT NOT NULL,
 source_mtime_ns BIGINT NOT NULL,
 source_sha256 TEXT,
 legacy_attachment_id UUID REFERENCES item_attachments(id) ON DELETE SET NULL,
 created_by UUID NOT NULL REFERENCES users(id),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 UNIQUE(item_id,source,source_path), UNIQUE(legacy_attachment_id)
);
CREATE INDEX IF NOT EXISTS nas_file_references_item ON nas_file_references(item_id);
