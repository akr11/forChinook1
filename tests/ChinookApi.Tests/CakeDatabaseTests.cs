using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ChinookApi.Checkers;
using Microsoft.AspNetCore.Builder;

namespace ChinookApi.Tests;

public class CakeDatabaseTests
{
    static readonly string DbDirectory = FindDb();

    [Fact]
    public void FourKingsAgainstOneIsAWinForBlack()
    {
        using var db = Open();
        Assert.True(Pdn.TryParse("B:WK1:BK29,BK30,BK31,BK32", out var black, out var error), error);
        Assert.Equal(CakeDatabase.Win, db.Lookup(black));
        Assert.True(db.TrySearch(black, new EndgameBook(0), out var found));
        Assert.Equal(1, found.ScoreOrWdl);
        Assert.True(found.TablebaseHit);
        Assert.Contains(found.BestMove, Rules.LegalMoves(black).Select(move => move.ToPdn()));

        Assert.True(Pdn.TryParse("W:WK1:BK29,BK30,BK31,BK32", out var white, out error), error);
        Assert.Equal(CakeDatabase.Loss, db.Lookup(white));
        Assert.True(db.TrySearch(white, new EndgameBook(0), out found));
        Assert.Equal(-1, found.ScoreOrWdl);
        Assert.Contains(found.BestMove, Rules.LegalMoves(white).Select(move => move.ToPdn()));
    }

    [Fact]
    public void CompressedSliceAgreesWithItsMirror()
    {
        using var db = Open();
        Assert.True(Pdn.TryParse("B:WK31,WK32:BK1,BK2,BK3", out var board, out var error), error);
        Assert.DoesNotContain(Rules.LegalMoves(board), move => move.CapLength > 0);
        var value = db.Lookup(board);
        Assert.Contains(value, new[] { CakeDatabase.Win, CakeDatabase.Loss, CakeDatabase.Draw });
        Assert.Equal(value, db.Lookup(board));
        Assert.Equal(value, db.Lookup(Mirror(board)));

        var clock = Stopwatch.StartNew();
        Assert.True(db.TrySearch(board, new EndgameBook(0), out var found));
        clock.Stop();
        Assert.True(found.TablebaseHit);
        Assert.Contains(found.BestMove, Rules.LegalMoves(board).Select(move => move.ToPdn()));
        Assert.True(clock.ElapsedMilliseconds < 50, $"probe took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SixPiecePositionIsInTheDatabase()
    {
        using var db = Open();
        Assert.True(Pdn.TryParse("B:WK30,WK31,WK32:BK1,BK2,BK3", out var board, out var error), error);
        Assert.DoesNotContain(Rules.LegalMoves(board), move => move.CapLength > 0);
        var value = db.Lookup(board);
        Assert.Contains(value, new[] { CakeDatabase.Win, CakeDatabase.Loss, CakeDatabase.Draw });
        Assert.Equal(value, db.Lookup(Mirror(board)));
        Assert.True(db.TrySearch(board, new EndgameBook(0), out var found));
        Assert.True(found.TablebaseHit);
        Assert.Contains(found.BestMove, Rules.LegalMoves(board).Select(move => move.ToPdn()));
    }

    [Fact]
    public void SevenPiecePositionIsInTheDatabase()
    {
        using var db = Open();
        Assert.True(db.MaxPieces >= 7);
        Assert.True(Pdn.TryParse("B:WK29,WK30,WK31,WK32:BK1,BK2,BK3", out var board, out var error), error);
        Assert.DoesNotContain(Rules.LegalMoves(board), move => move.CapLength > 0);
        var value = db.Lookup(board);
        Assert.Contains(value, new[] { CakeDatabase.Win, CakeDatabase.Loss, CakeDatabase.Draw });
        Assert.Equal(value, db.Lookup(Mirror(board)));
        var clock = Stopwatch.StartNew();
        Assert.True(db.TrySearch(board, new EndgameBook(0), out var found));
        clock.Stop();
        Assert.True(found.TablebaseHit);
        Assert.Contains(found.BestMove, Rules.LegalMoves(board).Select(move => move.ToPdn()));
        Assert.True(clock.ElapsedMilliseconds < 50, $"probe took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void EightPiecePositionIsInTheDatabase()
    {
        using var db = Open();
        Assert.True(db.MaxPieces >= 8);
        Assert.True(Pdn.TryParse("B:WK29,WK30,WK31,WK32:BK1,BK2,BK3,BK4", out var board, out var error), error);
        Assert.DoesNotContain(Rules.LegalMoves(board), move => move.CapLength > 0);
        var value = db.Lookup(board);
        Assert.Contains(value, new[] { CakeDatabase.Win, CakeDatabase.Loss, CakeDatabase.Draw });
        Assert.Equal(value, db.Lookup(Mirror(board)));
        var clock = Stopwatch.StartNew();
        Assert.True(db.TrySearch(board, new EndgameBook(0), out var found));
        clock.Stop();
        Assert.True(found.TablebaseHit);
        Assert.Contains(found.BestMove, Rules.LegalMoves(board).Select(move => move.ToPdn()));
        Assert.True(clock.ElapsedMilliseconds < 50, $"probe took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void CaptureIntoAWipeIsATablebaseWin()
    {
        using var db = Open();
        Assert.True(Pdn.TryParse("W:W22,K29,K30,K31:B18", out var board, out var error), error);
        Assert.True(db.TrySearch(board, new EndgameBook(0), out var found));
        Assert.Equal("22x15", found.BestMove);
        Assert.Equal(1, found.ScoreOrWdl);
        Assert.True(found.TablebaseHit);
    }

    static BoardState Mirror(BoardState board)
    {
        var mirrored = new BoardState { WhiteToMove = !board.WhiteToMove };
        for (var square = 0; square < 32; square++)
        {
            mirrored.Sq[31 - square] = board.Sq[square] switch
            {
                Piece.WhiteMan => Piece.BlackMan,
                Piece.WhiteKing => Piece.BlackKing,
                Piece.BlackMan => Piece.WhiteMan,
                Piece.BlackKing => Piece.WhiteKing,
                _ => Piece.Empty
            };
        }

        return mirrored;
    }

    static CakeDatabase Open()
    {
        var db = CakeDatabase.TryOpen(DbDirectory);
        Assert.NotNull(db);
        Assert.True(db!.MaxPieces >= 6);
        return db;
    }

    static string FindDb()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var nested = Path.Combine(dir.FullName, "ChinookApi", "db");
            if (File.Exists(Path.Combine(nested, "db5.idx")))
                return nested;
            if (File.Exists(Path.Combine(dir.FullName, "db", "db5.idx")))
                return Path.Combine(dir.FullName, "db");
        }

        throw new InvalidOperationException("db5.idx was not found");
    }
}

public class CakeApiTests : IAsyncLifetime
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
            ["Engine:TablebaseMaxPieces"] = "4",
            ["Engine:Databases"] = CakeDatabaseTestsDir()
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
    public async Task SuggestsAPerfectMoveForSixOrFewerPieces()
    {
        var warmup = await Suggest("B:WK1:BK29,BK30,BK31,BK32");
        Assert.True(warmup.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());

        var clock = Stopwatch.StartNew();
        var body = await Suggest("B:WK31,WK32:BK1,BK2,BK3");
        clock.Stop();
        Assert.True(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.Equal("chinook", body.GetProperty("engine").GetString());
        var move = body.GetProperty("bestMove").GetString();
        Assert.Contains(move, new[] { "1-5", "1-6", "2-6", "2-7", "3-7", "3-8" });
        var score = body.GetProperty("scoreOrWDL").GetInt32();
        Assert.Contains(score, new[] { -1, 0, 1 });
        Assert.True(clock.ElapsedMilliseconds < 50, $"suggest took {clock.ElapsedMilliseconds} ms");
    }

    async Task<JsonElement> Suggest(string position)
    {
        var response = await _client!.PostAsJsonAsync("/v1/move/suggest", new
        {
            gameId = "checkers-8x8",
            state = new { notation = "PDN", position },
            level = "strong"
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<JsonElement>(text, Json);
    }

    static string CakeDatabaseTestsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var nested = Path.Combine(dir.FullName, "ChinookApi", "db");
            if (File.Exists(Path.Combine(nested, "db5.idx")))
                return nested;
        }

        throw new InvalidOperationException("db5.idx was not found");
    }
}
