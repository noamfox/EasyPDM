using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace EasyPDM.Api.Tests;

[Collection("EasyPDM database")]
public class ExternalFileReferenceTests
{
    [Fact]
    public async Task Reference_capabilities_require_a_session()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/file-reference-capabilities")).StatusCode);
    }

    [Fact]
    public async Task Default_installation_keeps_upload_mode_and_does_not_expose_reference_routes()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new {username="admin",password="admin"})).EnsureSuccessStatusCode();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/file-reference-capabilities");
        Assert.False(capabilities.GetProperty("enabled").GetBoolean());
        var storage = await client.GetFromJsonAsync<JsonElement>("/api/settings/storage");
        Assert.Equal("upload", storage.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/items/{Guid.NewGuid()}/nas-references", new {path="models/sample.step"})).StatusCode);
    }
}
