using System.Numerics;

namespace ChinookApi.Checkers;

/// <summary>
/// Win/loss/draw probe for the Cake/Chinook endgame files (dbN.idx + dbN.cpr).
/// Capture positions are not stored; those are resolved by playing the captures.
/// </summary>
public sealed class CakeDatabase : IDisposable
{
    public const int Unknown = 0;
    public const int Win = 1;
    public const int Loss = 2;
    public const int Draw = 3;

    const int MaxPerSide = 4;

    static readonly int[] Skip =
    [
        5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        36, 40, 44, 48, 52, 56, 60, 70, 80, 90, 100, 150, 200, 250, 300, 400, 500, 650, 800, 1000, 1200, 1400,
        1600, 2000, 2400, 3200, 4000, 5000, 7500, 10000
    ];

    static readonly int[] Run = new int[256];
    static readonly int[] ByteValue = new int[256];
    static readonly uint[] Rev = new uint[65536];
    static readonly int[,] Binomial = new int[33, 33];

    readonly object _gate = new();
    readonly List<FileStream> _files = [];
    readonly List<long> _lengths = [];
    readonly Slice?[] _slices = new Slice?[5 * 5 * 5 * 5 * 7 * 7 * 2];
    readonly Dictionary<long, byte[]> _cache = new();
    readonly Queue<long> _cacheOrder = new();
    readonly int _cacheCap;

    static CakeDatabase()
    {
        for (var i = 0; i < 81; i++)
            Run[i] = 4;
        for (var i = 81; i < 256; i++)
        {
            Run[i] = Skip[(i - 81) % Skip.Length];
            ByteValue[i] = (i - 81) / Skip.Length;
        }

        for (var word = 0; word < Rev.Length; word++)
        {
            uint reversed = 0;
            for (var bit = 0; bit < 16; bit++)
                if ((word & (1 << bit)) != 0)
                    reversed |= 1u << (15 - bit);
            Rev[word] = reversed;
        }

        for (var n = 0; n < 33; n++)
        {
            Binomial[n, 0] = 1;
            for (var k = 1; k <= n; k++)
                Binomial[n, k] = Choose(n, k);
        }
    }

    CakeDatabase(string directory)
    {
        foreach (var idxPath in Directory.GetFiles(directory, "db*.idx").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(idxPath);
            if (!TryPieceCount(name, out var pieces))
                continue;
            var cprPath = Path.ChangeExtension(idxPath, ".cpr");
            if (!File.Exists(cprPath))
                continue;

            var file = new FileStream(cprPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            var fileId = _files.Count;
            _files.Add(file);
            _lengths.Add(file.Length);
            MaxPieces = Math.Max(MaxPieces, pieces);
            ParseIndex(File.ReadAllText(idxPath), fileId);
        }

        _cacheCap = MaxPieces >= 7 ? 65536 : 8192;
    }

    public int MaxPieces { get; }
    public int SliceCount { get; private set; }

    public static CakeDatabase? TryOpen(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;
            var database = new CakeDatabase(directory);
            if (database.MaxPieces < 2 || database.SliceCount == 0)
            {
                database.Dispose();
                return null;
            }

            Console.Error.WriteLine($"[chinook-db] pieces={database.MaxPieces} slices={database.SliceCount} dir={directory}");
            return database;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[chinook-db] " + ex.Message);
            return null;
        }
    }

    public bool TrySearch(BoardState board, EndgameBook book, out SearchResult result)
    {
        result = new SearchResult();
        var count = board.PieceCount;
        if (count <= 0 || count > MaxPieces)
            return false;
        if (count <= 4 && book.TryProbe(board, out var bookHit))
        {
            result = FromHit(board, bookHit.BestMove, bookHit.ScoreOrWdl);
            return true;
        }

        var moves = Rules.LegalMoves(board);
        if (moves.Count == 0)
        {
            result = FromHit(board, "", -1);
            return true;
        }

        var root = Evaluate(board, book);
        if (root is not (Win or Loss or Draw))
            return false;

        string? best = null;
        foreach (var move in moves)
        {
            board.Make(move);
            int child;
            try
            {
                child = Evaluate(board, book);
            }
            finally
            {
                board.Unmake(move);
            }

            if (Invert(child) != root)
                continue;
            best = move.ToPdn();
            break;
        }

        if (best == null)
            return false;

        result = FromHit(board, best, ToScore(root));
        return true;
    }

    public int Lookup(BoardState board)
    {
        lock (_gate)
            return LookupLocked(board);
    }

    public void Dispose()
    {
        foreach (var file in _files)
            file.Dispose();
        _files.Clear();
    }

    int Evaluate(BoardState board, EndgameBook book)
    {
        var moves = Rules.LegalMoves(board);
        if (moves.Count == 0)
            return Loss;
        if (moves[0].CapLength == 0)
        {
            if (board.PieceCount <= 4 && book.TryProbe(board, out var hit))
                return FromScore(hit.ScoreOrWdl);
            return Lookup(board);
        }

        var best = Unknown;
        foreach (var move in moves)
        {
            board.Make(move);
            int child;
            try
            {
                child = Evaluate(board, book);
            }
            finally
            {
                board.Unmake(move);
            }

            var ours = Invert(child);
            if (Rank(ours) > Rank(best))
                best = ours;
            if (ours == Win)
                return Win;
        }

        return best;
    }

    int LookupLocked(BoardState board)
    {
        uint blackMen = 0, blackKings = 0, whiteMen = 0, whiteKings = 0;
        for (var square = 0; square < 32; square++)
        {
            var bit = 1u << square;
            switch (board.Sq[square])
            {
                case Piece.BlackMan:
                    blackMen |= bit;
                    break;
                case Piece.BlackKing:
                    blackKings |= bit;
                    break;
                case Piece.WhiteMan:
                    whiteMen |= bit;
                    break;
                case Piece.WhiteKing:
                    whiteKings |= bit;
                    break;
            }
        }

        var bm = BitOperations.PopCount(blackMen);
        var bk = BitOperations.PopCount(blackKings);
        var wm = BitOperations.PopCount(whiteMen);
        var wk = BitOperations.PopCount(whiteKings);
        var total = bm + bk + wm + wk;
        if (total > MaxPieces || bm + bk > MaxPerSide || wm + wk > MaxPerSide || bm + bk == 0 || wm + wk == 0)
            return Unknown;

        var bmRank = blackMen == 0 ? 0 : Msb(blackMen) / 4;
        var wmRank = whiteMen == 0 ? 0 : (31 - Lsb(whiteMen)) / 4;
        var color = board.WhiteToMove ? 1 : 0;
        var dominated = unchecked(((wm + wk - bm - bk) << 16) + ((wk - bk) << 8) + ((wmRank - bmRank) << 4) + color);
        if (dominated > 0)
        {
            var reversedBlackMen = Revert(whiteMen);
            var reversedBlackKings = Revert(whiteKings);
            var reversedWhiteMen = Revert(blackMen);
            var reversedWhiteKings = Revert(blackKings);
            blackMen = reversedBlackMen;
            blackKings = reversedBlackKings;
            whiteMen = reversedWhiteMen;
            whiteKings = reversedWhiteKings;
            (bm, wm) = (wm, bm);
            (bk, wk) = (wk, bk);
            (bmRank, wmRank) = (wmRank, bmRank);
            color ^= 1;
        }

        if ((uint)bm > MaxPerSide || (uint)bk > MaxPerSide || (uint)wm > MaxPerSide || (uint)wk > MaxPerSide)
            return Unknown;
        if ((uint)bmRank > 6 || (uint)wmRank > 6)
            return Unknown;

        var slice = _slices[Key(bm, bk, wm, wk, bmRank, wmRank, color)];
        if (slice == null)
            return Unknown;
        if (slice.Value != Unknown)
            return slice.Value;
        if (slice.Idx == null || slice.Idx.Length == 0)
            return Unknown;

        var index = PositionIndex(blackMen, blackKings, whiteMen, whiteKings, bm, bk, wm, wk, bmRank, wmRank);
        var size = DatabaseSize(bm, bk, wm, wk, bmRank, wmRank);
        if (size <= 0 || index >= (ulong)size)
            return Unknown;

        var blockNumber = FindBlock(slice.Idx, index);
        var fileBlock = slice.FirstBlock + blockNumber;
        var block = ReadBlock(slice.FileId, fileBlock);
        if (block == null)
            return Unknown;
        return Decode(slice, index, block, blockNumber);
    }

    static uint PositionIndex(
        uint blackMen,
        uint blackKings,
        uint whiteMen,
        uint whiteKings,
        int bm,
        int bk,
        int wm,
        int wk,
        int bmRank,
        int wmRank)
    {
        uint blackManIndex = 0, whiteManIndex = 0, blackKingIndex = 0, whiteKingIndex = 0;
        var placed = 1;
        var bits = blackMen;
        while (bits != 0)
        {
            var square = Lsb(bits);
            bits ^= 1u << square;
            blackManIndex += (uint)Binomial[square, placed];
            placed++;
        }

        placed = 1;
        bits = whiteMen;
        while (bits != 0)
        {
            var square = Msb(bits);
            bits ^= 1u << square;
            square = 31 - square;
            whiteManIndex += (uint)Binomial[square, placed];
            placed++;
        }

        placed = 1;
        bits = blackKings;
        while (bits != 0)
        {
            var square = Lsb(bits);
            bits ^= 1u << square;
            square -= BitOperations.PopCount((blackMen | whiteMen) & ((1u << square) - 1));
            blackKingIndex += (uint)Binomial[square, placed];
            placed++;
        }

        placed = 1;
        bits = whiteKings;
        while (bits != 0)
        {
            var square = Lsb(bits);
            bits ^= 1u << square;
            square -= BitOperations.PopCount((blackMen | blackKings | whiteMen) & ((1u << square) - 1));
            whiteKingIndex += (uint)Binomial[square, placed];
            placed++;
        }

        uint blackManRange = 1, whiteManRange = 1, blackKingRange = 1;
        if (bm != 0)
            blackManRange = (uint)(Binomial[4 * (bmRank + 1), bm] - Binomial[4 * bmRank, bm]);
        if (wm != 0)
            whiteManRange = (uint)(Binomial[4 * (wmRank + 1), wm] - Binomial[4 * wmRank, wm]);
        if (bk != 0)
            blackKingRange = (uint)Binomial[32 - bm - wm, bk];
        if (bmRank != 0)
            blackManIndex -= (uint)Binomial[4 * bmRank, bm];
        if (wmRank != 0)
            whiteManIndex -= (uint)Binomial[4 * wmRank, wm];

        return blackManIndex
               + whiteManIndex * blackManRange
               + blackKingIndex * blackManRange * whiteManRange
               + whiteKingIndex * blackManRange * whiteManRange * blackKingRange;
    }

    static long DatabaseSize(int bm, int bk, int wm, int wk, int bmRank, int wmRank)
    {
        long size = 1;
        if (bm != 0)
            size *= Binomial[4 * (bmRank + 1), bm] - Binomial[4 * bmRank, bm];
        if (wm != 0)
            size *= Binomial[4 * (wmRank + 1), wm] - Binomial[4 * wmRank, wm];
        if (bk != 0)
            size *= Binomial[32 - bm - wm, bk];
        if (wk != 0)
            size *= Binomial[32 - bm - wm - bk, wk];
        return size;
    }

    static int FindBlock(uint[] idx, uint index)
    {
        var low = 0;
        var high = idx.Length;
        while (high > low + 1)
        {
            var mid = (low + high) / 2;
            if (idx[mid] <= index)
                low = mid;
            else
                high = mid;
        }

        return low;
    }

    static int Decode(Slice slice, uint index, byte[] block, int blockNumber)
    {
        var idx = slice.Idx!;
        var reverse = false;
        if (idx.Length > blockNumber + 1 && idx[blockNumber + 1] - index < index - idx[blockNumber])
            reverse = true;

        int cursor;
        uint covered;
        if (reverse)
        {
            covered = idx[blockNumber + 1];
            cursor = 1023;
            while (covered > index && cursor >= 0)
            {
                covered -= (uint)Run[block[cursor]];
                cursor--;
            }

            if (cursor < 1023)
                cursor++;
        }
        else
        {
            covered = idx[blockNumber];
            cursor = blockNumber == 0 ? slice.StartByte : 0;
            while (covered <= index && cursor < 1024)
            {
                covered += (uint)Run[block[cursor]];
                cursor++;
            }

            if (cursor == 0)
                return Unknown;
            cursor--;
            covered -= (uint)Run[block[cursor]];
        }

        if ((uint)cursor >= 1024)
            return Unknown;

        var encoded = block[cursor];
        int raw;
        if (encoded > 80)
            raw = ByteValue[encoded];
        else
        {
            var offset = (int)(index - covered);
            raw = offset switch
            {
                0 => encoded % 3,
                1 => (encoded / 3) % 3,
                2 => (encoded / 9) % 3,
                3 => (encoded / 27) % 3,
                _ => -1
            };
            if (raw < 0)
                return Unknown;
        }

        raw++;
        return raw is Win or Loss or Draw ? raw : Unknown;
    }

    byte[]? ReadBlock(int fileId, int block)
    {
        if ((uint)fileId >= (uint)_files.Count || block < 0)
            return null;
        var key = ((long)fileId << 32) | (uint)block;
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var stream = _files[fileId];
        var position = (long)block * 1024;
        if (position < 0 || position >= _lengths[fileId])
            return null;

        stream.Position = position;
        var buffer = new byte[1024];
        var got = 0;
        while (got < buffer.Length)
        {
            var read = stream.Read(buffer, got, buffer.Length - got);
            if (read == 0)
                break;
            got += read;
        }

        if (got == 0)
            return null;

        if (_cache.Count >= _cacheCap && _cacheOrder.Count > 0)
            _cache.Remove(_cacheOrder.Dequeue());
        _cache[key] = buffer;
        _cacheOrder.Enqueue(key);
        return buffer;
    }

    void ParseIndex(string text, int fileId)
    {
        var i = 0;
        while (i < text.Length)
        {
            var at = text.IndexOf("BASE", i, StringComparison.Ordinal);
            if (at < 0)
                break;
            i = at + 4;
            if (!TryReadInt(text, ref i, out var bm) || !Expect(text, ref i, ',')
                || !TryReadInt(text, ref i, out var bk) || !Expect(text, ref i, ',')
                || !TryReadInt(text, ref i, out var wm) || !Expect(text, ref i, ',')
                || !TryReadInt(text, ref i, out var wk) || !Expect(text, ref i, ',')
                || !TryReadInt(text, ref i, out var bmRank) || !Expect(text, ref i, ',')
                || !TryReadInt(text, ref i, out var wmRank) || !Expect(text, ref i, ',')
                || i >= text.Length)
                continue;

            var colorChar = text[i++];
            if (!Expect(text, ref i, ':'))
                continue;
            if ((uint)bm > MaxPerSide || (uint)bk > MaxPerSide || (uint)wm > MaxPerSide || (uint)wk > MaxPerSide)
                continue;
            if ((uint)bmRank > 6 || (uint)wmRank > 6)
                continue;

            var color = colorChar is 'b' or 'B' ? 0 : 1;
            var slice = new Slice { FileId = fileId };
            SkipSpace(text, ref i);
            if (i < text.Length && text[i] is '+' or '=' or '-')
            {
                slice.Value = text[i] switch
                {
                    '+' => Win,
                    '=' => Draw,
                    _ => Loss
                };
                i++;
            }
            else
            {
                if (!TryReadInt(text, ref i, out slice.FirstBlock) || !Expect(text, ref i, '/') || !TryReadInt(text, ref i, out slice.StartByte))
                    continue;
                var next = text.IndexOf("BASE", i, StringComparison.Ordinal);
                var end = next < 0 ? text.Length : next;
                var indices = new List<uint> { 0 };
                var cursor = i;
                while (cursor < end)
                {
                    while (cursor < end && !char.IsDigit(text[cursor]))
                        cursor++;
                    if (cursor >= end)
                        break;
                    var start = cursor;
                    while (cursor < end && char.IsDigit(text[cursor]))
                        cursor++;
                    if (uint.TryParse(text.AsSpan(start, cursor - start), out var entry))
                        indices.Add(entry);
                }

                slice.Idx = indices.ToArray();
                i = end;
            }

            _slices[Key(bm, bk, wm, wk, bmRank, wmRank, color)] = slice;
            SliceCount++;
        }
    }

    static SearchResult FromHit(BoardState board, string best, int score)
    {
        var line = string.IsNullOrEmpty(best) ? Array.Empty<string>() : new[] { best };
        var alternatives = new List<string>();
        if (line.Length > 0)
            alternatives.Add(line[0]);
        foreach (var move in Rules.LegalMoves(board))
        {
            var text = move.ToPdn();
            if (!alternatives.Contains(text))
                alternatives.Add(text);
        }

        return new SearchResult
        {
            BestMove = best,
            Pv = line,
            Alternatives = alternatives.ToArray(),
            ScoreOrWdl = score,
            Depth = 0,
            TablebaseHit = true
        };
    }

    static int Key(int bm, int bk, int wm, int wk, int bmRank, int wmRank, int color)
        => (((((bm * 5 + bk) * 5 + wm) * 5 + wk) * 7 + bmRank) * 7 + wmRank) * 2 + color;

    static int ToScore(int value) => value switch
    {
        Win => 1,
        Loss => -1,
        _ => 0
    };

    static int FromScore(int score) => score switch
    {
        > 0 => Win,
        < 0 => Loss,
        _ => Draw
    };

    static int Invert(int value) => value switch
    {
        Win => Loss,
        Loss => Win,
        Draw => Draw,
        _ => Unknown
    };

    static int Rank(int value) => value switch
    {
        Win => 3,
        Draw => 2,
        Loss => 1,
        _ => 0
    };

    static uint Revert(uint value) => Rev[value >> 16] | (Rev[value & 0xFFFF] << 16);

    static int Lsb(uint value) => BitOperations.TrailingZeroCount(value);

    static int Msb(uint value) => 31 - BitOperations.LeadingZeroCount(value);

    static int Choose(int n, int k)
    {
        if ((uint)k > (uint)n)
            return 0;
        long result = 1;
        for (var i = k; i > 0; i--)
            result *= n - i + 1;
        for (var i = k; i > 0; i--)
            result /= i;
        return (int)result;
    }

    static bool TryPieceCount(string name, out int pieces)
    {
        pieces = 0;
        if (!name.StartsWith("db", StringComparison.OrdinalIgnoreCase) || name.Length < 3 || !char.IsDigit(name[2]))
            return false;
        var end = 2;
        while (end < name.Length && char.IsDigit(name[end]))
            end++;
        return int.TryParse(name.AsSpan(2, end - 2), out pieces) && pieces is >= 2 and <= 8;
    }

    static bool TryReadInt(string text, ref int i, out int value)
    {
        SkipSpace(text, ref i);
        var start = i;
        while (i < text.Length && char.IsDigit(text[i]))
            i++;
        if (start == i)
        {
            value = 0;
            return false;
        }

        return int.TryParse(text.AsSpan(start, i - start), out value);
    }

    static bool Expect(string text, ref int i, char expected)
    {
        SkipSpace(text, ref i);
        if (i >= text.Length || text[i] != expected)
            return false;
        i++;
        return true;
    }

    static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
    }

    sealed class Slice
    {
        public int Value;
        public int FirstBlock;
        public int StartByte;
        public int FileId;
        public uint[]? Idx;
    }
}
