-- Migracja 059: usuwa zdublowane wpisy plików CAD i rysunków tej samej rewizji.
--
-- Pliki "cad"/"drawing" się kumulują między rewizjami, ale ponowna wysyłka TEJ SAMEJ
-- rewizji (ta sama nazwa "numer (nazwa).REWIZJA.rozszerzenie") dokładała dotąd kolejny
-- wiersz zamiast zastąpić poprzedni -- w panelu pojawiało się kilka identycznych
-- "…A.FCStd". Serwer już tego nie robi (zob. ReplaceExistingRoleAttachmentAsync); ta
-- migracja sprząta to, co zdążyło się nazbierać: zostaje NAJNOWSZY wpis każdej nazwy.
--
-- Pliki na dysku: przy wspólnym magazynie (makro + /register) wszystkie duplikaty
-- wskazywały ten sam, nadpisany plik, więc nic nie ginie. Duplikaty wgrane przez HTTP
-- miały własne kopie -- te stare kopie zostają w magazynie jako nieużywane pliki (SQL nie
-- usuwa plików); nie przeszkadzają w niczym.
-- Uruchom: psql -h localhost -U pdm_user -d pdm -f db/migrations/059_dedupe_cad_attachments.sql

BEGIN;

DELETE FROM item_attachments a
USING item_attachments newer
WHERE a.item_id = newer.item_id
  AND a.preview_role = newer.preview_role
  AND a.file_name = newer.file_name
  AND a.preview_role IN ('cad', 'drawing')
  AND (newer.uploaded_at, newer.id) > (a.uploaded_at, a.id);

COMMIT;
