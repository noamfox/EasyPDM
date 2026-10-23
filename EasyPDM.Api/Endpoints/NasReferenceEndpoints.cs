using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

// Original NAS files stay outside PDM storage. Only references enter the database.
static class NasReferenceEndpoints
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("EASYPDM_EXTERNAL_FILES") == "true";
    internal static string Source => Environment.GetEnvironmentVariable("EASYPDM_EXTERNAL_SOURCE") ?? "archive";
    internal static string PublicPath => Environment.GetEnvironmentVariable("EASYPDM_EXTERNAL_PUBLIC_PATH") ?? "/cad/";
    static readonly HttpClient Finder = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(Environment.GetEnvironmentVariable("EASYPDM_EXTERNAL_PROVIDER_URL") ?? "http://localhost:8091"), Timeout = TimeSpan.FromSeconds(30) };
    record LinkRequest(string Path, Guid? LegacyAttachmentId);
    static bool SafePath(string? p) => !string.IsNullOrWhiteSpace(p) && p.Length <= 2048 && !p.StartsWith('/') && !p.Contains('\\') && !p.Any(char.IsControl) && !p.Split('/').Any(x => x is "" or "." or "..");
    static bool SameOrigin(HttpContext c) => !c.Request.Headers.ContainsKey("Origin") || c.Request.Headers.Origin.ToString() == $"{c.Request.Scheme}://{c.Request.Host}";
    static async Task<HttpResponseMessage> Resolve(HttpContext ctx, string path, bool hash = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPath + "api/resolve");
        request.Headers.Add("Cookie", ctx.Request.Headers.Cookie.ToString());
        request.Content = JsonContent.Create(new { path, sha256 = hash });
        return await Finder.SendAsync(request);
    }
    static async Task<bool> Access(string cs, HttpContext ctx, Guid item)
    {
        var info = await ItemEndpoints.GetItemTypeAndStatus(cs, item);
        if (info is null) return false;
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
        return await ItemEndpoints.HasProjectAccessAsync(c, ctx, info.Value.ProjectId);
    }
    internal static async Task<IResult?> RedirectLegacy(string cs, HttpContext ctx, Guid legacy)
    {
        if (!Enabled) return null;
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id,item_id FROM nas_file_references WHERE legacy_attachment_id=@id", c);
        cmd.Parameters.AddWithValue("id", legacy);
        Guid id, item;
        await using (var r = await cmd.ExecuteReaderAsync()) { if (!await r.ReadAsync()) return null; id = r.GetGuid(0); item = r.GetGuid(1); }
        if (!await Access(cs, ctx, item)) return Results.StatusCode(403);
        return Results.Redirect($"/api/nas-references/{id}/file");
    }
    public static void MapNasReferenceEndpoints(this WebApplication app, string cs)
    {
        app.MapGet("/api/file-reference-capabilities", () => Results.Ok(new { enabled = Enabled, source = Source, publicPath = PublicPath }));
        if (!Enabled) return;
        if (!PublicPath.StartsWith('/') || PublicPath.StartsWith("//") || !PublicPath.EndsWith('/') || PublicPath.Contains('\\')) throw new InvalidOperationException("External public path must be an application-relative directory.");
        app.MapGet("/api/items/{itemId:guid}/nas-references", async (Guid itemId, HttpContext ctx) =>
        {
            if (!await Access(cs, ctx, itemId)) return Results.StatusCode(403);
            var rows = new List<Dictionary<string, object?>>();
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using (var cmd = new NpgsqlCommand("SELECT id,source_path,file_name,source_size,source_mtime_ns,source_sha256,legacy_attachment_id,created_at FROM nas_file_references WHERE item_id=@id ORDER BY created_at", c))
            {
                cmd.Parameters.AddWithValue("id", itemId);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) rows.Add(new() { ["id"] = r.GetGuid(0), ["source"] = Source, ["path"] = r.GetString(1), ["fileName"] = r.GetString(2), ["sourceSize"] = r.GetInt64(3), ["sourceMtimeNs"] = r.GetInt64(4), ["sourceSha256AtLink"] = r.IsDBNull(5) ? null : r.GetString(5), ["legacyAttachmentId"] = r.IsDBNull(6) ? null : r.GetGuid(6), ["createdAt"] = r.GetDateTime(7) });
            }
            foreach (var row in rows)
            {
                try
                {
                    using var response = await Resolve(ctx, (string)row["path"]!);
                    row["sourceStatus"] = response.IsSuccessStatusCode ? "available" : response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest ? "missing" : "unavailable";
                    if (response.IsSuccessStatusCode)
                    {
                        var d = await response.Content.ReadFromJsonAsync<JsonElement>();
                        if (d.GetProperty("size").GetInt64() != (long)row["sourceSize"]! || d.GetProperty("mtime_ns").GetInt64() != (long)row["sourceMtimeNs"]!) row["sourceStatus"] = "changed";
                    }
                }
                catch (HttpRequestException) { row["sourceStatus"] = "unavailable"; }
                catch (TaskCanceledException) { row["sourceStatus"] = "unavailable"; }
            }
            return Results.Ok(rows);
        });
        app.MapPost("/api/items/{itemId:guid}/nas-references", async (Guid itemId, LinkRequest body, HttpContext ctx) =>
        {
            if (!Enabled) return Results.StatusCode(409);
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            if (!SafePath(body.Path)) return Results.BadRequest("Use an exact relative source path.");
            var info = await ItemEndpoints.GetItemTypeAndStatus(cs, itemId);
            if (info is null) return Results.NotFound();
            if (!await Access(cs, ctx, itemId)) return Results.StatusCode(403);
            if (ItemEndpoints.IsLocked(info.Value.ItemType, info.Value.Status)) return Results.Conflict("Item must be In progress to change source links.");
            var user = (CurrentUser)ctx.Items["CurrentUser"]!;
            if (!ItemEndpoints.CanEditOwnerLocked(user.Id, info.Value.OwnerId, info.Value.OwnerLocked)) return ItemEndpoints.OwnerLockedForbidden();
            if (body.LegacyAttachmentId is not null && user.Role != "admin") return Results.StatusCode(403);
            using var source = await Resolve(ctx, body.Path, body.LegacyAttachmentId is not null);
            if (!source.IsSuccessStatusCode) return Results.BadRequest("Indexed NAS source is missing or unavailable; nothing linked.");
            var d = await source.Content.ReadFromJsonAsync<JsonElement>();
            string? sha = d.TryGetProperty("sha256", out var hv) ? hv.GetString() : null;
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            if (body.LegacyAttachmentId is Guid legacy)
            {
                await using var find = new NpgsqlCommand("SELECT file_path FROM item_attachments WHERE id=@id AND item_id=@item", c);
                find.Parameters.AddWithValue("id", legacy); find.Parameters.AddWithValue("item", itemId);
                var oldPath = await find.ExecuteScalarAsync() as string;
                if (oldPath is null || !File.Exists(oldPath)) return Results.BadRequest("Legacy attachment not found.");
                await using var old = File.OpenRead(oldPath);
                var oldSha = Convert.ToHexString(await SHA256.HashDataAsync(old)).ToLowerInvariant();
                if (sha is null || oldSha != sha) return Results.Conflict("NAS source and retained copy differ; no substitution made.");
            }
            await using var cmd = new NpgsqlCommand("""
              INSERT INTO nas_file_references(item_id,source,source_path,file_name,source_size,source_mtime_ns,source_sha256,legacy_attachment_id,created_by)
              VALUES(@item,@source,@path,@name,@size,@mtime,@sha,@legacy,@user)
              ON CONFLICT(item_id,source,source_path) DO UPDATE SET legacy_attachment_id=COALESCE(nas_file_references.legacy_attachment_id,EXCLUDED.legacy_attachment_id), source_sha256=COALESCE(nas_file_references.source_sha256,EXCLUDED.source_sha256)
              RETURNING id
              """, c);
            cmd.Parameters.AddWithValue("source", Source); cmd.Parameters.AddWithValue("item", itemId); cmd.Parameters.AddWithValue("path", body.Path); cmd.Parameters.AddWithValue("name", d.GetProperty("name").GetString()!);
            cmd.Parameters.AddWithValue("size", d.GetProperty("size").GetInt64()); cmd.Parameters.AddWithValue("mtime", d.GetProperty("mtime_ns").GetInt64());
            cmd.Parameters.AddWithValue("sha", NpgsqlDbType.Text, (object?)sha ?? DBNull.Value); cmd.Parameters.AddWithValue("legacy", NpgsqlDbType.Uuid, (object?)body.LegacyAttachmentId ?? DBNull.Value); cmd.Parameters.AddWithValue("user", user.Id);
            var id = (Guid)(await cmd.ExecuteScalarAsync())!;
            return Results.Ok(new { id, source = Source, path = body.Path, copiedBytes = 0, sourceSha256AtLink = sha });
        });
        // Reader resolves a reference ID through project authorization, never a caller file path.
        app.MapGet("/api/nas-references/{id:guid}/source", async (Guid id, HttpContext ctx) =>
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT item_id,source_path FROM nas_file_references WHERE id=@id", c); cmd.Parameters.AddWithValue("id",id);
            Guid item; string path;
            await using(var r=await cmd.ExecuteReaderAsync()){if(!await r.ReadAsync()) return Results.NotFound(); item=r.GetGuid(0);path=r.GetString(1);}
            if(!await Access(cs,ctx,item)) return Results.StatusCode(403);
            return Results.Ok(new { id, itemId=item, path, source=Source });
        });
        app.MapGet("/api/nas-references/{id:guid}/file", async (Guid id, HttpContext ctx) =>
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT item_id,source_path FROM nas_file_references WHERE id=@id", c); cmd.Parameters.AddWithValue("id", id);
            Guid item; string path;
            await using (var r = await cmd.ExecuteReaderAsync()) { if (!await r.ReadAsync()) return Results.NotFound(); item = r.GetGuid(0); path = r.GetString(1); }
            if (!await Access(cs, ctx, item)) return Results.StatusCode(403);
            return Results.Redirect(PublicPath + "api/source?reference=" + id);
        });
        app.MapDelete("/api/nas-references/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var find = new NpgsqlCommand("SELECT item_id FROM nas_file_references WHERE id=@id", c); find.Parameters.AddWithValue("id", id);
            var item = await find.ExecuteScalarAsync() as Guid?;
            if (item is null) return Results.NotFound();
            var info = await ItemEndpoints.GetItemTypeAndStatus(cs, item.Value);
            var user = (CurrentUser)ctx.Items["CurrentUser"]!;
            if (!await Access(cs, ctx, item.Value)) return Results.StatusCode(403);
            if (info is null || ItemEndpoints.IsLocked(info.Value.ItemType, info.Value.Status) || !ItemEndpoints.CanEditOwnerLocked(user.Id, info.Value.OwnerId, info.Value.OwnerLocked)) return Results.Conflict("Item is locked.");
            await using var del = new NpgsqlCommand("DELETE FROM nas_file_references WHERE id=@id", c); del.Parameters.AddWithValue("id", id); await del.ExecuteNonQueryAsync();
            return Results.Ok(new { unlinked = true, originalDeleted = false });
        });
    }
}
