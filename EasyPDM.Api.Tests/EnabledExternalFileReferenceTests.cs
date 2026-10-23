using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EasyPDM.Api.Tests;
[Collection("EasyPDM database")]
public class EnabledExternalFileReferenceTests
{
    sealed class Provider : HttpMessageHandler, IHttpClientFactory
    {
        public int Status = 200; public bool Malformed; public long Size = 3; public long Mtime = 10;
        public bool CookieSeen; public Func<Task>? BeforeReply;
        public string Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("CAD"))).ToLowerInvariant();
        public HttpClient CreateClient(string name) => new(this,false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            CookieSeen |= request.Headers.Contains("Cookie");
            if(BeforeReply is not null)await BeforeReply();
            if(Status==0)throw new HttpRequestException("offline");
            if(Status==-1)throw new TaskCanceledException("timeout");
            return new((HttpStatusCode)Status){Content=Malformed?new StringContent("{}",Encoding.UTF8,"application/json"):JsonContent.Create(new {name="sample.step",size=Size,mtime_ns=Mtime,sha256=Hash})};
        }
    }
    static WebApplicationFactory<Program> Enabled(EasyPDMWebApplicationFactory f,Provider provider) => f.WithWebHostBuilder(b=>{
        b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"EASYPDM_EXTERNAL_FILES","true"},{"EASYPDM_EXTERNAL_SOURCE","test-archive"},{"EASYPDM_EXTERNAL_ORIGIN","https://pdm.example.test"}}));
        b.ConfigureServices(s=>s.AddSingleton<IHttpClientFactory>(provider));
    });
    static async Task<Guid> Part(HttpClient c) {
        var project=await c.CreateProjectAsync("References "+Guid.NewGuid());
        var r=await c.PostAsJsonAsync($"/api/projects/{project}/nodes",new{name="Reference part",itemType="part",properties=new{rodzaj="Wykonywana"},parentId=(Guid?)null});r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    static async Task<Guid> Link(HttpClient c,Guid item,string path="models/sample.step",Guid? legacy=null){var r=await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path,legacyAttachmentId=legacy});r.EnsureSuccessStatusCode();var d=await r.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(0,d.GetProperty("copiedBytes").GetInt32());return d.GetProperty("id").GetGuid();}
    static async Task<JsonElement> Rows(HttpClient c,Guid item)=>await c.GetFromJsonAsync<JsonElement>($"/api/items/{item}/nas-references");
    [Fact]
    public async Task Links_are_idempotent_zero_copy_and_report_changed_missing_and_provider_failure()
    {
        await using var f=new EasyPDMWebApplicationFactory();var p=new Provider();await using var app=Enabled(f,p);using var c=app.CreateClient();await c.LoginAsync("admin","admin");var item=await Part(c);
        var files=Directory.GetFiles(f.StorageRoot,"*",SearchOption.AllDirectories);
        var ids=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Link(c,item)));
        Assert.Single(ids.Distinct());Assert.Single((await Rows(c,item)).EnumerateArray());Assert.True(p.CookieSeen);
        Assert.Equal(files,Directory.GetFiles(f.StorageRoot,"*",SearchOption.AllDirectories));
        Assert.Equal("available",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());
        p.Mtime++;Assert.Equal("changed",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());
        p.Status=404;Assert.Equal("missing",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());
        foreach(var status in new[]{503,0,-1}){p.Status=status;Assert.Equal("unavailable",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());Assert.Equal(HttpStatusCode.ServiceUnavailable,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/other.step"})).StatusCode);}
        p.Status=200;p.Malformed=true;Assert.Equal("unavailable",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());Assert.Equal(HttpStatusCode.ServiceUnavailable,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/other.step"})).StatusCode);
        p.Malformed=false;var removed=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>c.DeleteAsync($"/api/nas-references/{ids[0]}")));Assert.All(removed,r=>Assert.Contains(r.StatusCode,new[]{HttpStatusCode.OK,HttpStatusCode.NotFound}));
        Assert.Empty((await Rows(c,item)).EnumerateArray());Assert.Equal(files,Directory.GetFiles(f.StorageRoot,"*",SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Stored_source_cannot_be_reinterpreted_and_other_projects_cannot_read_or_mutate()
    {
        await using var f=new EasyPDMWebApplicationFactory();var p=new Provider();await using var app=Enabled(f,p);using var c=app.CreateClient(new(){AllowAutoRedirect=false});await c.LoginAsync("admin","admin");var item=await Part(c);var id=await Link(c,item);
        var username="outside"+Guid.NewGuid().ToString("N");await c.CreateUserAsync(username,"test-password");using var other=app.CreateClient();await other.LoginAsync(username,"test-password");
        foreach(var path in new[]{$"/api/items/{item}/nas-references",$"/api/nas-references/{id}/source",$"/api/nas-references/{id}/file"})Assert.Equal(HttpStatusCode.Forbidden,(await other.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await other.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/sample.step"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await other.DeleteAsync($"/api/nas-references/{id}")).StatusCode);
        await using var db=new NpgsqlConnection(EasyPDMWebApplicationFactory.ConnectionString);await db.OpenAsync();await using var cmd=new NpgsqlCommand("UPDATE nas_file_references SET source='different-root' WHERE id=@id",db);cmd.Parameters.AddWithValue("id",id);await cmd.ExecuteNonQueryAsync();
        Assert.Equal("different-root",(await Rows(c,item))[0].GetProperty("source").GetString());Assert.Equal("source-unavailable",(await Rows(c,item))[0].GetProperty("sourceStatus").GetString());
        Assert.Equal(HttpStatusCode.Conflict,(await c.GetAsync($"/api/nas-references/{id}/source")).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await c.GetAsync($"/api/nas-references/{id}/file")).StatusCode);
    }
    [Fact]
    public async Task Legacy_substitution_requires_matching_bytes_and_unlink_preserves_upload()
    {
        await using var f=new EasyPDMWebApplicationFactory();var p=new Provider();await using var app=Enabled(f,p);using var c=app.CreateClient(new(){AllowAutoRedirect=false});await c.LoginAsync("admin","admin");var item=await Part(c);
        var disk=Path.Combine(f.StorageRoot,"retained.step");await File.WriteAllTextAsync(disk,"CAD");var legacy=Guid.NewGuid();
        await using var db=new NpgsqlConnection(EasyPDMWebApplicationFactory.ConnectionString);await db.OpenAsync();await using(var cmd=new NpgsqlCommand("INSERT INTO item_attachments(id,item_id,file_name,file_path,file_size) VALUES(@id,@item,'retained.step',@path,3)",db)){cmd.Parameters.AddWithValue("id",legacy);cmd.Parameters.AddWithValue("item",item);cmd.Parameters.AddWithValue("path",disk);await cmd.ExecuteNonQueryAsync();}
        p.Hash=new string('0',64);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/sample.step",legacyAttachmentId=legacy})).StatusCode);Assert.Empty((await Rows(c,item)).EnumerateArray());
        p.Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("CAD"))).ToLowerInvariant();var id=await Link(c,item,legacy:legacy);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>($"/api/items/{item}/attachments")).EnumerateArray());Assert.Equal(HttpStatusCode.Found,(await c.GetAsync($"/api/attachments/{legacy}/file")).StatusCode);
        (await c.DeleteAsync($"/api/nas-references/{id}")).EnsureSuccessStatusCode();Assert.Equal("CAD",await File.ReadAllTextAsync(disk));Assert.Single((await c.GetFromJsonAsync<JsonElement>($"/api/items/{item}/attachments")).EnumerateArray());
    }
    [Fact]
    public async Task Item_state_is_rechecked_after_provider_IO_and_https_origin_is_explicit()
    {
        await using var f=new EasyPDMWebApplicationFactory();var p=new Provider();await using var app=Enabled(f,p);using var c=app.CreateClient();await c.LoginAsync("admin","admin");var item=await Part(c);
        c.DefaultRequestHeaders.Add("Origin","https://wrong.example.test");
        Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/sample.step"})).StatusCode);
        c.DefaultRequestHeaders.Remove("Origin");c.DefaultRequestHeaders.Add("Origin","https://pdm.example.test");
        foreach(var path in new[]{"../escape.step","/absolute.step","models//sample.step","models/./sample.step"})Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path})).StatusCode);
        var id=await Link(c,item);
        p.BeforeReply=async()=>{await using var db=new NpgsqlConnection(EasyPDMWebApplicationFactory.ConnectionString);await db.OpenAsync();await using var cmd=new NpgsqlCommand("UPDATE items SET status='wydany' WHERE id=@id",db);cmd.Parameters.AddWithValue("id",item);await cmd.ExecuteNonQueryAsync();};
        Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync($"/api/items/{item}/nas-references",new{path="models/late.step"})).StatusCode);
        p.BeforeReply=null;
        Assert.Equal(HttpStatusCode.Conflict,(await c.DeleteAsync($"/api/nas-references/{id}")).StatusCode);
        Assert.Single((await Rows(c,item)).EnumerateArray());
    }

}
