using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace ChinookApi.Tests;

public class ApiTests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    WebApplication? _app;
    HttpClient? _client;

    public async Task InitializeAsync()
    {
        _app = AppHost.Create([], new Dictionary<string, string?>
        {
            ["Engine:Mode"] = "inprocess",
            ["Engine:Workers"] = "1",
            ["Engine:TablebaseMaxPieces"] = "3",
            ["Limits:DefaultHardTimeMs"] = "2000"
        });
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First(url => url.StartsWith("http://"))) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null)
            await _app.StopAsync();
    }

    [Fact]
    public async Task HealthReportsAWorker()
    {
        var body = await _client!.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("workers").GetInt32() >= 1);
    }

    [Fact]
    public async Task InvalidPdnIs422()
    {
        var response = await _client!.PostAsJsonAsync("/v1/move/suggest", new
        {
            gameId = "checkers-8x8",
            state = new { notation = "PDN", position = "not-a-position" },
            level = "weak"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SuggestOpeningReturnsALegalMove()
    {
        const string position = "B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12";
        var response = await _client!.PostAsJsonAsync("/v1/move/suggest", new
        {
            gameId = "checkers-8x8",
            state = new { notation = "PDN", position },
            level = "weak",
            limits = new { maxDepth = 4, softTimeMs = 300, hardTimeMs = 2000 }
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text, Json);
        var move = body.GetProperty("bestMove").GetString();
        Assert.Contains(move, new[] { "9-13", "9-14", "10-14", "10-15", "11-15", "11-16", "12-16" });
        Assert.Equal("pdn:" + position, body.GetProperty("positionKey").GetString());
        Assert.False(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
    }

    [Fact]
    public async Task TablebasePositionIsMarked()
    {
        var response = await _client!.PostAsJsonAsync("/v1/move/suggest", new
        {
            gameId = "checkers-8x8",
            state = new { notation = "PDN", position = "W:W22:B18" },
            level = "strong"
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text, Json);
        Assert.Equal("22x15", body.GetProperty("bestMove").GetString());
        Assert.Equal(1, body.GetProperty("scoreOrWDL").GetInt32());
        Assert.True(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
    }

    [Fact]
    public async Task ValidateRejectsAQuietMoveWhenACaptureExists()
    {
        var response = await _client!.PostAsJsonAsync("/v1/move/validate", new
        {
            position = "W:W22:B18",
            move = "22-17"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("legal").GetBoolean());
    }
}
