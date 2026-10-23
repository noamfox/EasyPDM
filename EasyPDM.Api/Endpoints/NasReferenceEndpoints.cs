using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

// Original NAS files stay outside PDM storage. Only references enter the database.
static class NasReferenceEndpoints
{
    internal static bool Enabled(IConfiguration c) => c["EASYPDM_EXTERNAL_FILES"] == "true";
    internal static string Source(IConfiguration c) => c["EASYPDM_EXTERNAL_SOURCE"] ?? "archive";
    static string PublicPath(IConfiguration c) => c["EASYPDM_EXTERNAL_PUBLIC_PATH"] ?? "/cad/";
    record LinkRequest(string Path, Guid? LegacyAttachmentId);
    static bool SafePath(string? p) => !string.IsNullOrWhiteSpace(p) && p.Length <= 2048 && !p.StartsWith('/') && !p.Contains('\\') && !p.Any(char.IsControl) && !p.Split('/').Any(x => x is "" or "." or "..");
    static bool SameOrigin(HttpContext c) {
        var origin = c.Request.Headers.Origin.ToString();
        var configured = c.RequestServices.GetRequiredService<IConfiguration>()["EASYPDM_EXTERNAL_ORIGIN"];
        return origin.Length == 0 || origin == (configured ?? $"{c.Request.Scheme}://{c.Request.Host}");
    }
    static async Task<HttpResponseMessage> Resolve(HttpContext ctx, string path, bool hash = false)
    {
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        using var client = ctx.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("ExternalFiles");
        client.BaseAddress = new Uri(config["EASYPDM_EXTERNAL_PROVIDER_URL"] ?? "http://localhost:8091");
        client.Timeout = TimeSpan.FromSeconds(5);
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPath(config) + "api/resolve");
        request.Headers.Add("Cookie", ctx.Request.Headers.Cookie.ToString());
        request.Content = new StringContent(JsonSerializer.Serialize(new { path, sha256 = hash }), System.Text.Encoding.UTF8, "application/json");
        try {
            var response = await client.SendAsync(request, ctx.RequestAborted);
            if (!response.IsSuccessStatusCode) return response;
            try {
                var d = await response.Content.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                if (string.IsNullOrWhiteSpace(d.GetProperty("name").GetString()) || d.GetProperty("size").GetInt64() < 0 || d.GetProperty("mtime_ns").GetInt64() < 0) throw new JsonException();
                if (hash && (!d.TryGetProperty("sha256", out var h) || h.GetString() is not string digest || digest.Length != 64 || !digest.All(Uri.IsHexDigit))) throw new JsonException();
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(d) };
            } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
                response.Dispose(); return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
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
        if (!Enabled(ctx.RequestServices.GetRequiredService<IConfiguration>())) return null;
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
        var enabled = Enabled(app.Configuration);
        var sourceName = Source(app.Configuration);
        var publicPath = PublicPath(app.Configuration);
        app.MapGet("/api/file-reference-capabilities", () => Results.Ok(new { enabled, source = sourceName, publicPath }));
        if (!enabled) return;
        if (!publicPath.StartsWith('/') || publicPath.StartsWith("//") || !publicPath.EndsWith('/') || publicPath.Contains('\\')) throw new InvalidOperationException("External public path must be an application-relative directory.");
        app.MapGet("/api/items/{itemId:guid}/nas-references", async (Guid itemId, HttpContext ctx) =>
        {
            if (!await Access(cs, ctx, itemId)) return Results.StatusCode(403);
            var rows = new List<Dictionary<string, object?>>();
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using (var cmd = new NpgsqlCommand("SELECT id,source_path,file_name,source_size,source_mtime_ns,source_sha256,legacy_attachment_id,created_at,source FROM nas_file_references WHERE item_id=@id ORDER BY created_at", c))
            {
                cmd.Parameters.AddWithValue("id", itemId);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) rows.Add(new() { ["id"] = r.GetGuid(0), ["source"] = r.GetString(8), ["path"] = r.GetString(1), ["fileName"] = r.GetString(2), ["sourceSize"] = r.GetInt64(3), ["sourceMtimeNs"] = r.GetInt64(4), ["sourceSha256AtLink"] = r.IsDBNull(5) ? null : r.GetString(5), ["legacyAttachmentId"] = r.IsDBNull(6) ? null : r.GetGuid(6), ["createdAt"] = r.GetDateTime(7) });
            }
            foreach (var row in rows)
            {
                if ((string)row["source"]! != sourceName) { row["sourceStatus"] = "source-unavailable"; continue; }
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
            if (!enabled) return Results.StatusCode(409);
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
            if (!source.IsSuccessStatusCode) return source.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest
                ? Results.BadRequest("Source is missing; nothing linked.") : Results.Json(new { error = "Source provider unavailable; nothing linked." }, statusCode: 503);
            var d = await source.Content.ReadFromJsonAsync<JsonElement>();
            string? sha = d.TryGetProperty("sha256", out var hv) ? hv.GetString() : null;
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using (var gate = new NpgsqlCommand("SELECT id FROM items WHERE id=@id FOR UPDATE", c, tx)) { gate.Parameters.AddWithValue("id",itemId); if(await gate.ExecuteScalarAsync() is null)return Results.NotFound(); }
            info = await ItemEndpoints.GetItemTypeAndStatus(cs, itemId);
            if(info is null || !await Access(cs,ctx,itemId))return Results.StatusCode(403);
            if(ItemEndpoints.IsLocked(info.Value.ItemType,info.Value.Status) || !ItemEndpoints.CanEditOwnerLocked(user.Id,info.Value.OwnerId,info.Value.OwnerLocked))return Results.Conflict("Item state changed while resolving source.");
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
              WHERE nas_file_references.legacy_attachment_id IS NULL OR EXCLUDED.legacy_attachment_id IS NULL OR nas_file_references.legacy_attachment_id=EXCLUDED.legacy_attachment_id
              RETURNING id
              """, c);
            cmd.Parameters.AddWithValue("source", sourceName); cmd.Parameters.AddWithValue("item", itemId); cmd.Parameters.AddWithValue("path", body.Path); cmd.Parameters.AddWithValue("name", d.GetProperty("name").GetString()!);
            cmd.Parameters.AddWithValue("size", d.GetProperty("size").GetInt64()); cmd.Parameters.AddWithValue("mtime", d.GetProperty("mtime_ns").GetInt64());
            cmd.Parameters.AddWithValue("sha", NpgsqlDbType.Text, (object?)sha ?? DBNull.Value); cmd.Parameters.AddWithValue("legacy", NpgsqlDbType.Uuid, (object?)body.LegacyAttachmentId ?? DBNull.Value); cmd.Parameters.AddWithValue("user", user.Id);
            Guid id;
            try { var value=await cmd.ExecuteScalarAsync(); if(value is not Guid found)return Results.Conflict("Source is already mapped to a different retained attachment."); id=found; }
            catch (PostgresException ex) when(ex.SqlState=="23505") { return Results.Conflict("Attachment is already linked to another source."); }
            await tx.CommitAsync();
            return Results.Ok(new { id, source = sourceName, path = body.Path, copiedBytes = 0, sourceSha256AtLink = sha });
        });
        // Reader resolves a reference ID through project authorization, never a caller file path.
        app.MapGet("/api/nas-references/{id:guid}/source", async (Guid id, HttpContext ctx) =>
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT item_id,source_path,source FROM nas_file_references WHERE id=@id", c); cmd.Parameters.AddWithValue("id",id);
            Guid item; string path;
            await using(var r=await cmd.ExecuteReaderAsync()){if(!await r.ReadAsync()) return Results.NotFound(); item=r.GetGuid(0);path=r.GetString(1);if(r.GetString(2)!=sourceName)return Results.Conflict("Configured source differs from the stored reference.");}
            if(!await Access(cs,ctx,item)) return Results.StatusCode(403);
            return Results.Ok(new { id, itemId=item, path, source=sourceName });
        });
        app.MapGet("/api/nas-references/{id:guid}/file", async (Guid id, HttpContext ctx) =>
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT item_id,source_path,source FROM nas_file_references WHERE id=@id", c); cmd.Parameters.AddWithValue("id", id);
            Guid item; string path;
            await using (var r = await cmd.ExecuteReaderAsync()) { if (!await r.ReadAsync()) return Results.NotFound(); item = r.GetGuid(0); path = r.GetString(1); if(r.GetString(2)!=sourceName)return Results.Conflict("Configured source differs from the stored reference."); }
            if (!await Access(cs, ctx, item)) return Results.StatusCode(403);
            return Results.Redirect(publicPath + "api/source?reference=" + id);
        });
        app.MapDelete("/api/nas-references/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var find = new NpgsqlCommand("SELECT item_id FROM nas_file_references WHERE id=@id", c); find.Parameters.AddWithValue("id", id);
            var item = await find.ExecuteScalarAsync() as Guid?;
            if (item is null) return Results.NotFound();
            await using var tx = await c.BeginTransactionAsync();
            await using (var gate = new NpgsqlCommand("SELECT id FROM items WHERE id=@id FOR UPDATE",c,tx)) { gate.Parameters.AddWithValue("id",item.Value); if(await gate.ExecuteScalarAsync() is null)return Results.NotFound(); }
            var info = await ItemEndpoints.GetItemTypeAndStatus(cs, item.Value);
            var user = (CurrentUser)ctx.Items["CurrentUser"]!;
            if (!await Access(cs, ctx, item.Value)) return Results.StatusCode(403);
            if (info is null || ItemEndpoints.IsLocked(info.Value.ItemType, info.Value.Status) || !ItemEndpoints.CanEditOwnerLocked(user.Id, info.Value.OwnerId, info.Value.OwnerLocked)) return Results.Conflict("Item is locked.");
            await using var del = new NpgsqlCommand("DELETE FROM nas_file_references WHERE id=@id", c); del.Parameters.AddWithValue("id", id); var removed=await del.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            if(removed==0)return Results.NotFound();
            return Results.Ok(new { unlinked = true, originalDeleted = false });
        });
    }
}
