namespace ChinookApi.Checkers;

public static class Piece
{
    public const byte Empty = 0;
    public const byte WhiteMan = 1;
    public const byte WhiteKing = 2;
    public const byte BlackMan = 3;
    public const byte BlackKing = 4;

    public static bool IsWhite(byte piece) => piece is WhiteMan or WhiteKing;

    public static bool IsKing(byte piece) => piece is WhiteKing or BlackKing;
}

public readonly struct Step
{
    public readonly short To;
    public readonly short Jump;

    public Step(int to, int jump)
    {
        To = (short)to;
        Jump = (short)jump;
    }
}

public static class Squares
{
    public static readonly Step[][] King = new Step[32][];
    public static readonly Step[][] WhiteMan = new Step[32][];
    public static readonly Step[][] BlackMan = new Step[32][];

    static Squares()
    {
        var kingDirs = new (int Dc, int Dr)[] { (-1, -1), (1, -1), (-1, 1), (1, 1) };
        var whiteDirs = new (int Dc, int Dr)[] { (-1, -1), (1, -1) };
        var blackDirs = new (int Dc, int Dr)[] { (-1, 1), (1, 1) };
        for (var i = 0; i < 32; i++)
        {
            King[i] = Steps(i, kingDirs);
            WhiteMan[i] = Steps(i, whiteDirs);
            BlackMan[i] = Steps(i, blackDirs);
        }
    }

    public static int Row(int index) => index / 4;

    public static bool Promotes(bool white, int index) => white ? index < 4 : index >= 28;

    static Step[] Steps(int index, (int Dc, int Dr)[] dirs)
    {
        var (col, row) = Coord(index);
        var list = new List<Step>(4);
        foreach (var (dc, dr) in dirs)
        {
            var to = Index(col + dc, row + dr);
            if (to < 0)
                continue;
            var jump = Index(col + 2 * dc, row + 2 * dr);
            list.Add(new Step(to, jump));
        }

        return list.ToArray();
    }

    public static (int Col, int Row) Coord(int index)
    {
        var row = index / 4;
        var col = (index % 4) * 2 + ((row & 1) == 0 ? 1 : 0);
        return (col, row);
    }

    public static int Index(int col, int row)
    {
        if ((uint)row > 7 || (uint)col > 7)
            return -1;
        var dark = (row & 1) == 0 ? (col & 1) == 1 : (col & 1) == 0;
        if (!dark)
            return -1;
        return row * 4 + col / 2;
    }
}

public sealed class Move
{
    public int From;
    public int To;
    public int OldPiece;
    public int NewPiece;
    public int CapLength;
    public readonly int[] CapSq = new int[12];
    public readonly int[] CapPiece = new int[12];
    public int PathLength;
    public readonly int[] Path = new int[12];

    public string ToPdn()
    {
        if (CapLength == 0)
            return $"{From + 1}-{To + 1}";

        var text = (From + 1).ToString();
        for (var i = 0; i < PathLength; i++)
            text += "x" + (Path[i] + 1);
        return text;
    }
}

public sealed class BoardState
{
    public readonly byte[] Sq = new byte[32];
    public bool WhiteToMove;

    public int PieceCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < 32; i++)
                if (Sq[i] != Piece.Empty)
                    n++;
            return n;
        }
    }

    public BoardState Clone()
    {
        var copy = new BoardState { WhiteToMove = WhiteToMove };
        Array.Copy(Sq, copy.Sq, 32);
        return copy;
    }

    public void Make(Move move)
    {
        Sq[move.From] = Piece.Empty;
        for (var i = 0; i < move.CapLength; i++)
            Sq[move.CapSq[i]] = Piece.Empty;
        Sq[move.To] = (byte)move.NewPiece;
        WhiteToMove = !WhiteToMove;
    }

    public void Unmake(Move move)
    {
        Sq[move.To] = Piece.Empty;
        Sq[move.From] = (byte)move.OldPiece;
        for (var i = move.CapLength - 1; i >= 0; i--)
            Sq[move.CapSq[i]] = (byte)move.CapPiece[i];
        WhiteToMove = !WhiteToMove;
    }

    public string ToPdn()
    {
        var white = new List<string>();
        var black = new List<string>();
        for (var i = 0; i < 32; i++)
        {
            var piece = Sq[i];
            if (piece == Piece.Empty)
                continue;
            var token = piece is Piece.WhiteKing or Piece.BlackKing ? $"K{i + 1}" : (i + 1).ToString();
            if (Piece.IsWhite(piece))
                white.Add(token);
            else
                black.Add(token);
        }

        return $"{(WhiteToMove ? "W" : "B")}:W{string.Join(',', white)}:B{string.Join(',', black)}";
    }
}

public static class Rules
{
    public static List<Move> LegalMoves(BoardState board)
    {
        var moves = new List<Move>(16);
        Generate(board, moves);
        return moves;
    }

    public static void Generate(BoardState board, List<Move> into)
    {
        var before = into.Count;
        for (var i = 0; i < 32; i++)
        {
            var piece = board.Sq[i];
            if (piece == Piece.Empty || Piece.IsWhite(piece) != board.WhiteToMove)
                continue;
            CollectJumps(board, i, piece, into);
        }

        if (into.Count > before)
            return;

        for (var i = 0; i < 32; i++)
        {
            var piece = board.Sq[i];
            if (piece == Piece.Empty || Piece.IsWhite(piece) != board.WhiteToMove)
                continue;
            var steps = StepsFor(piece);
            foreach (var step in steps[i])
            {
                if (board.Sq[step.To] != Piece.Empty)
                    continue;
                var promoted = !Piece.IsKing(piece) && Squares.Promotes(board.WhiteToMove, step.To);
                var next = promoted
                    ? (board.WhiteToMove ? Piece.WhiteKing : Piece.BlackKing)
                    : piece;
                var move = new Move
                {
                    From = i,
                    To = step.To,
                    OldPiece = piece,
                    NewPiece = next,
                    PathLength = 1
                };
                move.Path[0] = step.To;
                into.Add(move);
            }
        }
    }

    public static bool TryMatch(BoardState board, string notation, out Move? move, out string error)
    {
        move = null;
        error = "";
        if (!TrySquares(notation, out var squares, out error))
            return false;

        var legal = LegalMoves(board);
        Move? found = null;
        var matches = 0;
        foreach (var candidate in legal)
        {
            if (!SameMove(candidate, squares))
                continue;
            matches++;
            found = candidate;
        }

        if (matches == 1 && found != null)
        {
            move = found;
            return true;
        }

        if (matches > 1)
        {
            error = "Move is ambiguous; include the full capture path";
            return false;
        }

        error = "Move is not legal";
        return false;
    }

    static bool SameMove(Move move, int[] squares)
    {
        if (squares.Length < 2 || squares[0] != move.From)
            return false;
        if (squares.Length == 2)
            return squares[1] == move.To;
        if (move.CapLength == 0 || squares[^1] != move.To || squares.Length != move.PathLength + 1)
            return false;
        for (var i = 0; i < move.PathLength; i++)
            if (squares[i + 1] != move.Path[i])
                return false;
        return true;
    }

    public static bool TrySquares(string notation, out int[] squares, out string error)
    {
        squares = [];
        error = "";
        if (string.IsNullOrWhiteSpace(notation))
        {
            error = "Move is empty";
            return false;
        }

        var parts = notation.Trim().Split(['-', 'x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "Move must look like 11-15 or 22x15";
            return false;
        }

        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var number) || number < 1 || number > 32)
            {
                error = "Squares must be from 1 to 32";
                return false;
            }

            parsed[i] = number - 1;
        }

        squares = parsed;
        return true;
    }

    static void CollectJumps(BoardState board, int from, byte piece, List<Move> into)
    {
        var path = new int[12];
        var caps = new int[12];
        var capPieces = new int[12];
        Walk(board, from, from, piece, path, 0, caps, capPieces, 0, into);
    }

    static void Walk(
        BoardState board,
        int origin,
        int from,
        byte piece,
        int[] path,
        int pathLength,
        int[] caps,
        int[] capPieces,
        int capLength,
        List<Move> into)
    {
        var continued = false;
        var steps = StepsFor(piece);
        var white = Piece.IsWhite(piece);
        foreach (var step in steps[from])
        {
            if (step.Jump < 0)
                continue;
            var victim = board.Sq[step.To];
            if (victim == Piece.Empty || Piece.IsWhite(victim) == white)
                continue;
            if (board.Sq[step.Jump] != Piece.Empty)
                continue;

            continued = true;
            var landing = step.Jump;
            var promoted = !Piece.IsKing(piece) && Squares.Promotes(white, landing);
            var next = promoted ? (byte)(white ? Piece.WhiteKing : Piece.BlackKing) : piece;
            board.Sq[from] = Piece.Empty;
            board.Sq[step.To] = Piece.Empty;
            board.Sq[landing] = next;
            path[pathLength] = landing;
            caps[capLength] = step.To;
            capPieces[capLength] = victim;

            if (promoted)
                Emit(origin, piece, path, pathLength + 1, caps, capPieces, capLength + 1, into);
            else
                Walk(board, origin, landing, piece, path, pathLength + 1, caps, capPieces, capLength + 1, into);

            board.Sq[landing] = Piece.Empty;
            board.Sq[step.To] = victim;
            board.Sq[from] = piece;
        }

        if (!continued && capLength > 0)
            Emit(origin, piece, path, pathLength, caps, capPieces, capLength, into);
    }

    static void Emit(int origin, byte piece, int[] path, int pathLength, int[] caps, int[] capPieces, int capLength, List<Move> into)
    {
        var last = path[pathLength - 1];
        var white = Piece.IsWhite(piece);
        var newPiece = !Piece.IsKing(piece) && Squares.Promotes(white, last)
            ? (white ? Piece.WhiteKing : Piece.BlackKing)
            : piece;
        var move = new Move
        {
            From = origin,
            To = last,
            OldPiece = piece,
            NewPiece = newPiece,
            CapLength = capLength,
            PathLength = pathLength
        };
        Array.Copy(path, move.Path, pathLength);
        Array.Copy(caps, move.CapSq, capLength);
        Array.Copy(capPieces, move.CapPiece, capLength);
        into.Add(move);
    }

    static Step[][] StepsFor(byte piece) => piece switch
    {
        Piece.WhiteMan => Squares.WhiteMan,
        Piece.BlackMan => Squares.BlackMan,
        _ => Squares.King
    };
}

public static class Pdn
{
    public static bool TryParse(string? text, out BoardState board, out string error)
    {
        board = new BoardState();
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "PDN is empty";
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 3)
        {
            error = "PDN must look like B:W18,22:B1,5";
            return false;
        }

        var side = parts[0].Trim().ToUpperInvariant();
        if (side is not ("W" or "B"))
        {
            error = "Side to move must be W or B";
            return false;
        }

        if (!TryList(parts[1], whiteList: true, board, out error))
            return false;
        if (!TryList(parts[2], whiteList: false, board, out error))
            return false;

        var whiteCount = 0;
        var blackCount = 0;
        for (var i = 0; i < 32; i++)
        {
            if (Piece.IsWhite(board.Sq[i]))
                whiteCount++;
            else if (board.Sq[i] != Piece.Empty)
                blackCount++;
        }

        if (whiteCount > 12 || blackCount > 12)
        {
            error = "A side cannot have more than 12 pieces";
            return false;
        }

        board.WhiteToMove = side == "W";
        return true;
    }

    static bool TryList(string section, bool whiteList, BoardState board, out string error)
    {
        error = "";
        var body = section.Trim();
        if (body.Length == 0)
        {
            error = "Piece list is missing the W or B marker";
            return false;
        }

        var marker = char.ToUpperInvariant(body[0]);
        if (whiteList && marker != 'W' || !whiteList && marker != 'B')
        {
            error = whiteList ? "White pieces must start with W" : "Black pieces must start with B";
            return false;
        }

        body = body[1..];
        if (body.Length == 0)
            return true;

        foreach (var token in body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var text = token;
            var king = false;
            if (text.StartsWith("WK", StringComparison.OrdinalIgnoreCase) || text.StartsWith("BK", StringComparison.OrdinalIgnoreCase))
            {
                king = true;
                text = text[2..];
            }
            else if (text.StartsWith('K') || text.StartsWith('k'))
            {
                king = true;
                text = text[1..];
            }

            if (!int.TryParse(text, out var number) || number < 1 || number > 32)
            {
                error = $"Invalid square '{token}'";
                return false;
            }

            var index = number - 1;
            if (board.Sq[index] != Piece.Empty)
            {
                error = $"Square {number} is occupied twice";
                return false;
            }

            if (!king && whiteList && index < 4)
            {
                error = $"White man cannot stand on square {number}";
                return false;
            }

            if (!king && !whiteList && index >= 28)
            {
                error = $"Black man cannot stand on square {number}";
                return false;
            }

            board.Sq[index] = whiteList
                ? (king ? Piece.WhiteKing : Piece.WhiteMan)
                : (king ? Piece.BlackKing : Piece.BlackMan);
        }

        return true;
    }
}
