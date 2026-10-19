using System.Net.Http.Json;
using System.Text.Json;

namespace EasyPDM.Api.Tests;

// Komponenty złożenia wysyłane z makra CAD nie mają w przeglądarce wyboru projektu ani
// rodzica: ich miejsce w strukturze wynika z drzewa CAD. Powstają bez projektu, a do
// projektu trafiają, gdy makro podpina je pod złożenie (POST /items/{id}/children) --
// serwer przypisuje im wtedy projekt rodzica. Wcześniej każdą część dało się w formularzu
// dodać w dowolne miejsce i struktura w BOM przestawała odpowiadać złożeniu.
[Collection("EasyPDM database")]
public class AssemblyComponentProjectTests
{
    private const string AdminUsername = "admin";
    private const string AdminPassword = "admin";

    private static async Task<Guid> CreateWithoutProjectAsync(HttpClient client, string name, string itemType)
    {
        var response = await client.PostAsJsonAsync("/api/nodes", new
        {
            name,
            itemType,
            properties = new { rodzaj = itemType == "part" ? "Wykonywana" : "Wykonywane" },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AttachAsync(HttpClient client, Guid parentId, Guid childId) =>
        (await client.PostAsJsonAsync($"/api/items/{parentId}/children", new { childId, quantity = 1 }))
            .EnsureSuccessStatusCode();

    private static async Task<(Guid? ProjectId, bool ShowInTree)> ReadAsync(HttpClient client, Guid itemId)
    {
        var items = await client.GetFromJsonAsync<JsonElement>("/api/items");
        var item = items.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId);
        var project = item.GetProperty("projectId");
        return (project.ValueKind == JsonValueKind.Null ? null : project.GetGuid(),
            item.GetProperty("showInTree").GetBoolean());
    }

    [Fact]
    public async Task Komponent_bez_projektu_przejmuje_projekt_zlozenia_razem_z_podzespolami()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);

        // Kolejność jak w makrze: najpierw liście, potem pod-złożenie, na końcu element główny.
        var part = await CreateWithoutProjectAsync(client, $"Czesc {Guid.NewGuid()}", "part");
        var subAssembly = await CreateWithoutProjectAsync(client, $"Podzespol {Guid.NewGuid()}", "assembly");
        await AttachAsync(client, subAssembly, part);

        var projectId = await client.CreateProjectAsync($"Projekt {Guid.NewGuid()}");
        var top = await client.CreateNodeAsync(projectId, "Zlozenie glowne", "assembly");
        await AttachAsync(client, top, subAssembly);

        Assert.Equal((projectId, false), await ReadAsync(client, subAssembly));
        Assert.Equal((projectId, false), await ReadAsync(client, part));
    }

    [Fact]
    public async Task Element_z_innym_projektem_zostaje_w_swoim()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);

        var ownProject = await client.CreateProjectAsync($"Wlasny {Guid.NewGuid()}");
        var shared = await client.CreateNodeAsync(ownProject, "Wspolna czesc", "part");

        var otherProject = await client.CreateProjectAsync($"Inny {Guid.NewGuid()}");
        var top = await client.CreateNodeAsync(otherProject, "Zlozenie", "assembly");
        await AttachAsync(client, top, shared);

        // Część współdzielona między projektami nie jest przenoszona -- przejmowany jest tylko
        // brakujący projekt, nigdy nie nadpisywany istniejący.
        Assert.Equal((ownProject, true), await ReadAsync(client, shared));
    }

    [Fact]
    public async Task Prosba_makra_przenosi_oznaczenie_komponentu()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);

        (await client.PutAsJsonAsync("/api/cad-requests", new
        {
            runId = "bieg-1",
            ticket = "bilet-komponentu",
            mode = "create",
            name = "Kolumna",
            component = true,
        })).EnsureSuccessStatusCode();

        var request = (await client.GetFromJsonAsync<JsonElement>("/api/cad-requests")).GetProperty("request");
        Assert.True(request.GetProperty("component").GetBoolean());

        // Starsze makra pola nie wysyłają -- wtedy to zwykły element, z wyborem projektu.
        (await client.PutAsJsonAsync("/api/cad-requests", new
        {
            runId = "bieg-1",
            ticket = "bilet-glowny",
            mode = "create",
        })).EnsureSuccessStatusCode();
        request = (await client.GetFromJsonAsync<JsonElement>("/api/cad-requests")).GetProperty("request");
        Assert.False(request.GetProperty("component").GetBoolean());
    }
}
