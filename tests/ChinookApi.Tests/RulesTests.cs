using ChinookApi.Checkers;

namespace ChinookApi.Tests;

public class RulesTests
{
    [Fact]
    public void OpeningHasSevenMoves()
    {
        var board = Parse("B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12");
        var moves = Rules.LegalMoves(board).Select(move => move.ToPdn()).ToHashSet();
        Assert.True(moves.SetEquals(["9-13", "9-14", "10-14", "10-15", "11-15", "11-16", "12-16"]));
    }

    [Fact]
    public void CaptureIsMandatory()
    {
        var board = Parse("W:W22:B18");
        var moves = Rules.LegalMoves(board).Select(move => move.ToPdn()).ToArray();
        Assert.Equal(["22x15"], moves);
    }

    [Fact]
    public void PromotionEndsTheJump()
    {
        var board = Parse("W:W10:B6");
        var move = Assert.Single(Rules.LegalMoves(board));
        Assert.Equal("10x1", move.ToPdn());
        board.Make(move);
        Assert.Equal(Piece.WhiteKing, board.Sq[0]);
        Assert.Equal("B:WK1:B", board.ToPdn());
    }

    [Fact]
    public void InvalidManOnPromotionRankIsRejected()
    {
        Assert.False(Pdn.TryParse("W:W1:B", out _, out var error));
        Assert.Contains("White man", error);
    }

    [Fact]
    public void TablebaseSeesImmediateCaptureWin()
    {
        var board = Parse("W:W22:B18");
        var book = new EndgameBook(4);
        Assert.True(book.TryProbe(board, out var hit));
        Assert.Equal("22x15", hit.BestMove);
        Assert.Equal(1, hit.ScoreOrWdl);
    }

    [Fact]
    public void TwoKingsAreADraw()
    {
        var board = Parse("W:WK32:BK1");
        var book = new EndgameBook(2);
        Assert.True(book.TryProbe(board, out var hit));
        Assert.Equal(0, hit.ScoreOrWdl);
        Assert.False(string.IsNullOrEmpty(hit.BestMove));
    }

    static BoardState Parse(string pdn)
    {
        Assert.True(Pdn.TryParse(pdn, out var board, out var error), error);
        return board;
    }
}
