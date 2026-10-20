using System.Net.Http.Json;
using System.Text.Json;

namespace EasyPDM.Api.Tests;

// Folder albo plik po "Usuń ze struktury" zostaje w bazie bez projektu. Okno "Istniejący
// element" pozwala go wstawić z powrotem -- pod folder albo do korzenia projektu -- i wtedy
// jego zawartość bez projektu ma pójść razem z nim, inaczej folder wracałby pusty dla
// filtrów i uprawnień projektu.
[Collection("EasyPDM database")]
public class ReuseDetachedFolderTests
{
    private const string AdminUsername = "admin";
    private const string AdminPassword = "admin";

    private static async Task<Guid> CreateFolderWithoutProjectAsync(HttpClient client, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync("/api/nodes", new { name, itemType = "folder", parentId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid? ProjectId, bool ShowInTree)> ReadAsync(HttpClient client, Guid itemId)
    {
        var items = await client.GetFromJsonAsync<JsonElement>("/api/items");
        var item = items.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId);
        var project = item.GetProperty("projectId");
        return (project.ValueKind == JsonValueKind.Null ? null : project.GetGuid(),
            item.GetProperty("showInTree").GetBoolean());
    }

    [Fact]
    public async Task Folder_wstawiony_pod_folder_projektu_zabiera_zawartosc()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);

        var detached = await CreateFolderWithoutProjectAsync(client, $"Dokumentacja {Guid.NewGuid()}");
        var inner = await CreateFolderWithoutProjectAsync(client, "Rysunki", detached);

        var projectId = await client.CreateProjectAsync($"Projekt {Guid.NewGuid()}");
        var target = await client.CreateNodeAsync(projectId, "Folder docelowy", "folder");
        (await client.PostAsJsonAsync($"/api/items/{target}/children", new { childId = detached, quantity = 1 }))
            .EnsureSuccessStatusCode();

        Assert.Equal((projectId, false), await ReadAsync(client, detached));
        Assert.Equal((projectId, false), await ReadAsync(client, inner));
    }

    [Fact]
    public async Task Folder_przeniesiony_do_korzenia_projektu_zabiera_zawartosc()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);

        var detached = await CreateFolderWithoutProjectAsync(client, $"Dokumentacja {Guid.NewGuid()}");
        var inner = await CreateFolderWithoutProjectAsync(client, "Rysunki", detached);
        var projectId = await client.CreateProjectAsync($"Projekt {Guid.NewGuid()}");

        (await client.PatchAsJsonAsync($"/api/items/{detached}/project", new { projectId })).EnsureSuccessStatusCode();

        // Sam folder wraca jako widoczny korzeń, jego zawartość -- jako zawartość, nie luzem.
        Assert.Equal((projectId, true), await ReadAsync(client, detached));
        Assert.Equal((projectId, false), await ReadAsync(client, inner));
    }
}
