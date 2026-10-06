namespace ChinookApi.Checkers;

public sealed class SearchResult
{
    public string BestMove { get; init; } = "";
    public string[] Pv { get; init; } = [];
    public string[] Alternatives { get; init; } = [];
    public int ScoreOrWdl { get; init; }
    public int Depth { get; init; }
    public long Nodes { get; init; }
    public bool TablebaseHit { get; init; }
    public string Engine { get; init; } = "chinook";
}

public sealed class SearchEngine
{
    readonly EndgameBook _book;
    readonly CakeDatabase? _cake;
    readonly ulong[,] _zobrist = new ulong[32, 5];
    readonly ulong _sideKey;

    public SearchEngine(EndgameBook book, CakeDatabase? cake = null)
    {
        _book = book;
        _cake = cake;
        var random = new Random(20260329);
        for (var sq = 0; sq < 32; sq++)
            for (var piece = 1; piece <= 4; piece++)
                _zobrist[sq, piece] = unchecked((ulong)random.NextInt64());
        _sideKey = unchecked((ulong)random.NextInt64());
    }

    public SearchResult Search(BoardState board, int maxDepth, int softTimeMs, bool probeTablebase)
    {
        if (probeTablebase && _cake != null && _cake.TrySearch(board, _book, out var databaseResult))
            return databaseResult;

        if (probeTablebase && _book.TryProbe(board, out var hit))
        {
            var probeLine = string.IsNullOrEmpty(hit.BestMove) ? Array.Empty<string>() : new[] { hit.BestMove };
            return new SearchResult
            {
                BestMove = hit.BestMove,
                Pv = probeLine,
                Alternatives = probeLine,
                ScoreOrWdl = hit.ScoreOrWdl,
                Depth = 0,
                Nodes = 0,
                TablebaseHit = true
            };
        }

        var rootMoves = Rules.LegalMoves(board);
        if (rootMoves.Count == 0)
        {
            return new SearchResult
            {
                ScoreOrWdl = -1,
                Depth = 0,
                TablebaseHit = probeTablebase && board.PieceCount <= 4
            };
        }

        maxDepth = Math.Clamp(maxDepth, 1, 32);
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + MsToTicks(Math.Max(1, softTimeMs));
        long nodes = 0;
        var stopped = false;
        var bestMove = rootMoves[0];
        var bestScore = 0;
        var completedDepth = 0;
        var pv = new Move[33][];
        for (var i = 0; i < pv.Length; i++)
            pv[i] = new Move[33];
        var pvLen = new int[33];
        var seen = new HashSet<ulong>();

        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var windowBest = int.MinValue / 4;
            Move? windowMove = null;
            pvLen[0] = 0;
            var ordered = Order(rootMoves, bestMove);
            foreach (var move in ordered)
            {
                board.Make(move);
                var score = -Negamax(board, depth - 1, 1, int.MinValue / 4, int.MaxValue / 4, deadline, seen, pv, pvLen, ref nodes, ref stopped);
                board.Unmake(move);
                if (stopped && windowMove != null)
                    break;
                if (score > windowBest)
                {
                    windowBest = score;
                    windowMove = move;
                    pv[0][0] = move;
                    for (var i = 0; i < pvLen[1]; i++)
                        pv[0][i + 1] = pv[1][i];
                    pvLen[0] = pvLen[1] + 1;
                }

                if (stopped)
                    break;
            }

            if (windowMove != null && (!stopped || completedDepth == 0))
            {
                bestMove = windowMove;
                bestScore = windowScore(windowBest);
                completedDepth = depth;
            }

            if (stopped)
                break;
        }

        var line = new string[Math.Max(1, pvLen[0])];
        if (pvLen[0] == 0)
            line[0] = bestMove.ToPdn();
        else
            for (var i = 0; i < pvLen[0]; i++)
                line[i] = pv[0][i].ToPdn();

        var alternatives = new List<string>(rootMoves.Count) { bestMove.ToPdn() };
        foreach (var move in rootMoves)
        {
            var text = move.ToPdn();
            if (!alternatives.Contains(text))
                alternatives.Add(text);
        }

        return new SearchResult
        {
            BestMove = bestMove.ToPdn(),
            Pv = line,
            Alternatives = alternatives.ToArray(),
            ScoreOrWdl = bestScore,
            Depth = completedDepth,
            Nodes = nodes
        };

        int windowScore(int score) => score;
    }

    int Negamax(
        BoardState board,
        int depth,
        int ply,
        int alpha,
        int beta,
        long deadline,
        HashSet<ulong> seen,
        Move[][] pv,
        int[] pvLen,
        ref long nodes,
        ref bool stopped)
    {
        nodes++;
        if ((nodes & 127) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
        {
            stopped = true;
            return Evaluate(board);
        }

        var key = Hash(board);
        if (!seen.Add(key))
            return 0;

        var moves = Rules.LegalMoves(board);
        if (moves.Count == 0)
        {
            seen.Remove(key);
            return -100_000 + ply;
        }

        if (depth == 0)
        {
            seen.Remove(key);
            return Evaluate(board);
        }

        var best = int.MinValue / 4;
        pvLen[ply] = 0;
        foreach (var move in Order(moves, null))
        {
            board.Make(move);
            var score = -Negamax(board, depth - 1, ply + 1, -beta, -alpha, deadline, seen, pv, pvLen, ref nodes, ref stopped);
            board.Unmake(move);
            if (score > best)
            {
                best = score;
                alpha = Math.Max(alpha, score);
                pv[ply][0] = move;
                for (var i = 0; i < pvLen[ply + 1]; i++)
                    pv[ply][i + 1] = pv[ply + 1][i];
                pvLen[ply] = pvLen[ply + 1] + 1;
            }

            if (alpha >= beta || stopped)
                break;
        }

        seen.Remove(key);
        return best;
    }

    static List<Move> Order(List<Move> moves, Move? preferred)
    {
        moves.Sort((a, b) =>
        {
            var pref = (b == preferred ? 1 : 0) - (a == preferred ? 1 : 0);
            if (pref != 0)
                return pref;
            return b.CapLength.CompareTo(a.CapLength);
        });
        return moves;
    }

    static int Evaluate(BoardState board)
    {
        var score = 0;
        for (var i = 0; i < 32; i++)
        {
            var row = Squares.Row(i);
            switch (board.Sq[i])
            {
                case Piece.WhiteMan:
                    score += 100 + (7 - row) * 4;
                    break;
                case Piece.WhiteKing:
                    score += 175;
                    break;
                case Piece.BlackMan:
                    score -= 100 + row * 4;
                    break;
                case Piece.BlackKing:
                    score -= 175;
                    break;
            }
        }

        return board.WhiteToMove ? score : -score;
    }

    ulong Hash(BoardState board)
    {
        ulong hash = board.WhiteToMove ? _sideKey : 0;
        for (var i = 0; i < 32; i++)
        {
            var piece = board.Sq[i];
            if (piece != Piece.Empty)
                hash ^= _zobrist[i, piece];
        }

        return hash;
    }

    static long MsToTicks(int ms) => (long)(ms * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
}
