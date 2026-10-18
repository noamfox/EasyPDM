using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EasyPDM.Api.Tests;

// Pliki CAD (i rysunki) się kumulują, ale tylko między rewizjami: makra nazywają plik
// "numer (nazwa).REWIZJA.rozszerzenie", więc ta sama nazwa to ta sama rewizja wysłana
// jeszcze raz i ma zastąpić poprzedni wpis. Wcześniej każda ponowna wysyłka dokładała
// wiersz -- w panelu stały trzy identyczne "…A.FCStd", a przy wspólnym magazynie wszystkie
// wskazywały ten sam plik, więc usunięcie jednego kasowało plik spod pozostałych.
[Collection("EasyPDM database")]
public class CadAttachmentRevisionTests
{
    private const string AdminUsername = "admin";
    private const string AdminPassword = "admin";

    private static async Task<Guid> CreatePartAsync(HttpClient client, Guid projectId)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/nodes", new
        {
            name = $"Element {Guid.NewGuid()}",
            itemType = "part",
            properties = new { rodzaj = "Wykonywana" },
            parentId = (Guid?)null,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task UploadAsync(HttpClient client, Guid itemId, string role, string fileName, string content)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName },
            { new StringContent(role), "role" },
        };
        (await client.PostAsync($"/api/items/{itemId}/attachments", form)).EnsureSuccessStatusCode();
    }

    private static async Task<List<(Guid Id, string FileName, string? Role)>> AttachmentsAsync(HttpClient client, Guid itemId)
    {
        var response = await client.GetAsync($"/api/items/{itemId}/attachments");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray()
            .Select(a => (a.GetProperty("id").GetGuid(),
                a.GetProperty("fileName").GetString()!,
                a.GetProperty("role").ValueKind == JsonValueKind.Null ? null : a.GetProperty("role").GetString()))
            .ToList();
    }

    [Fact]
    public async Task Ponowna_wysylka_tej_samej_rewizji_zastepuje_wpis()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);
        var itemId = await CreatePartAsync(client, await client.CreateProjectAsync($"CAD {Guid.NewGuid()}"));

        await UploadAsync(client, itemId, "cad", "10(Kolumna).A.FCStd", "pierwsza");
        await UploadAsync(client, itemId, "cad", "10(Kolumna).A.FCStd", "druga");
        await UploadAsync(client, itemId, "cad", "10(Kolumna).A.FCStd", "trzecia");

        var cad = (await AttachmentsAsync(client, itemId)).Where(a => a.Role == "cad").ToList();
        Assert.Single(cad);
        var content = await client.GetStringAsync($"/api/attachments/{cad[0].Id}/file");
        Assert.Equal("trzecia", content);
    }

    [Fact]
    public async Task Nowa_rewizja_zostaje_obok_poprzedniej()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);
        var itemId = await CreatePartAsync(client, await client.CreateProjectAsync($"CAD {Guid.NewGuid()}"));

        await UploadAsync(client, itemId, "cad", "10(Kolumna).A.FCStd", "rewizja A");
        await UploadAsync(client, itemId, "cad", "10(Kolumna).B.FCStd", "rewizja B");

        var names = (await AttachmentsAsync(client, itemId)).Where(a => a.Role == "cad").Select(a => a.FileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("10(Kolumna).A.FCStd", names);
        Assert.Contains("10(Kolumna).B.FCStd", names);
    }

    // Ścieżka makr przy wspólnym magazynie: plik jest kopiowany w miejsce poprzedniego
    // (ta sama ścieżka) i rejestrowany. Stary wiersz ma zniknąć, ale pliku -- który jest
    // już nową wersją -- nie wolno skasować.
    [Fact]
    public async Task Ponowna_rejestracja_tej_samej_rewizji_nie_kasuje_pliku()
    {
        await using var factory = new EasyPDMWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsync(AdminUsername, AdminPassword);
        var itemId = await CreatePartAsync(client, await client.CreateProjectAsync($"CAD {Guid.NewGuid()}"));

        var dir = Path.Combine(factory.StorageRoot, "components");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}(Kolumna).A.FCStd");

        await File.WriteAllTextAsync(path, "pierwsza");
        (await client.PostAsJsonAsync($"/api/items/{itemId}/attachments/register", new { filePath = path, role = "cad" }))
            .EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(path, "druga");
        (await client.PostAsJsonAsync($"/api/items/{itemId}/attachments/register", new { filePath = path, role = "cad" }))
            .EnsureSuccessStatusCode();

        var cad = (await AttachmentsAsync(client, itemId)).Where(a => a.Role == "cad").ToList();
        Assert.Single(cad);
        Assert.True(File.Exists(path));
        Assert.Equal("druga", await client.GetStringAsync($"/api/attachments/{cad[0].Id}/file"));
    }
}
