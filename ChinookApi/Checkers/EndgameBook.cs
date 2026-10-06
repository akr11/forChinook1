namespace ChinookApi.Checkers;

public readonly struct ProbeHit
{
    public string BestMove { get; init; }
    public int ScoreOrWdl { get; init; }
}

public sealed class EndgameBook
{
    const int IndexBits = 26;
    const int IndexMask = (1 << IndexBits) - 1;

    readonly int _maxPieces;
    readonly Layer?[] _layers;

    public EndgameBook(int maxPieces)
    {
        _maxPieces = Math.Clamp(maxPieces, 0, 4);
        _layers = new Layer?[_maxPieces + 1];
    }

    public bool TryProbe(BoardState board, out ProbeHit hit)
    {
        hit = default;
        var count = Count(board.Sq);
        if (count == 0 || count > _maxPieces)
            return false;

        Ensure(count);
        var layer = _layers[count];
        if (layer == null || !layer.Index.TryGetValue(new PosKey(board.Sq, board.WhiteToMove), out var id))
            return false;

        hit = new ProbeHit
        {
            BestMove = layer.Best[id] ?? "",
            ScoreOrWdl = layer.State[id] switch
            {
                1 => 1,
                2 => -1,
                _ => 0
            }
        };
        return true;
    }

    void Ensure(int count)
    {
        for (var n = 1; n <= count; n++)
        {
            if (_layers[n] != null)
                continue;
            var started = DateTime.UtcNow;
            _layers[n] = Build(n);
            Console.Error.WriteLine($"[tablebase] pieces={n} positions={_layers[n]!.Keys.Length} ms={(int)(DateTime.UtcNow - started).TotalMilliseconds}");
        }
    }

    Layer Build(int count)
    {
        var keys = new List<PosKey>(1 << 12);
        var index = new Dictionary<PosKey, int>(1 << 12);
        Enumerate(count, keys, index);

        var state = new byte[keys.Count];
        var heads = new int[keys.Count];
        Array.Fill(heads, -1);
        var next = new List<int>(keys.Count);
        var child = new List<int>(keys.Count);
        var scratch = new BoardState();
        var moves = new List<Move>(16);

        for (var id = 0; id < keys.Count; id++)
        {
            keys[id].Write(scratch.Sq, out scratch.WhiteToMove);
            moves.Clear();
            Rules.Generate(scratch, moves);
            if (moves.Count == 0)
            {
                state[id] = 2;
                continue;
            }

            foreach (var move in moves)
            {
                scratch.Make(move);
                var childCount = Count(scratch.Sq);
                var childKey = new PosKey(scratch.Sq, scratch.WhiteToMove);
                scratch.Unmake(move);
                int childId;
                if (childCount == count)
                {
                    if (!index.TryGetValue(childKey, out childId))
                        continue;
                }
                else if (_layers[childCount] == null || !_layers[childCount]!.Index.TryGetValue(childKey, out childId))
                {
                    continue;
                }

                next.Add(heads[id]);
                child.Add((childCount << IndexBits) | childId);
                heads[id] = next.Count - 1;
            }
        }

        var nextArr = next.ToArray();
        var childArr = child.ToArray();
        var guard = 0;
        var changed = true;
        while (changed && guard++ < keys.Count + 2)
        {
            changed = false;
            for (var id = 0; id < keys.Count; id++)
            {
                if (state[id] != 0)
                    continue;

                var anyLoss = false;
                var allWin = true;
                var any = false;
                for (var link = heads[id]; link >= 0; link = nextArr[link])
                {
                    any = true;
                    var childState = StateOf(childArr[link], count, state);
                    if (childState == 2)
                        anyLoss = true;
                    else if (childState != 1)
                        allWin = false;
                }

                if (!any)
                    continue;
                if (anyLoss)
                {
                    state[id] = 1;
                    changed = true;
                }
                else if (allWin)
                {
                    state[id] = 2;
                    changed = true;
                }
            }
        }

        for (var id = 0; id < state.Length; id++)
            if (state[id] == 0)
                state[id] = 3;

        var best = new string?[keys.Count];
        for (var id = 0; id < keys.Count; id++)
        {
            keys[id].Write(scratch.Sq, out scratch.WhiteToMove);
            moves.Clear();
            Rules.Generate(scratch, moves);
            Move? chosenMove = null;
            foreach (var move in moves)
            {
                scratch.Make(move);
                var childCount = Count(scratch.Sq);
                var childKey = new PosKey(scratch.Sq, scratch.WhiteToMove);
                scratch.Unmake(move);
                int childId;
                if (childCount == count)
                {
                    if (!index.TryGetValue(childKey, out childId))
                        continue;
                }
                else if (_layers[childCount] == null || !_layers[childCount]!.Index.TryGetValue(childKey, out childId))
                {
                    continue;
                }

                var childState = StateOf((childCount << IndexBits) | childId, count, state);
                if (state[id] == 1 && childState == 2)
                {
                    chosenMove = move;
                    break;
                }

                if (state[id] == 3 && childState == 3)
                    chosenMove ??= move;
                chosenMove ??= move;
            }

            best[id] = chosenMove?.ToPdn() ?? "";
        }

        return new Layer(index, keys.ToArray(), state, best);
    }

    byte StateOf(int packed, int currentCount, byte[] currentState)
    {
        var layer = packed >> IndexBits;
        var id = packed & IndexMask;
        if (layer == currentCount)
            return currentState[id];
        return _layers[layer]!.State[id];
    }

    static int Count(byte[] sq)
    {
        var n = 0;
        for (var i = 0; i < 32; i++)
            if (sq[i] != Piece.Empty)
                n++;
        return n;
    }

    static void Enumerate(int count, List<PosKey> keys, Dictionary<PosKey, int> index)
    {
        var chosen = new int[count];
        var board = new byte[32];
        void Rec(int placed, int next)
        {
            if (placed == count)
            {
                var combos = 1 << (count * 2);
                for (var mask = 0; mask < combos; mask++)
                {
                    Array.Clear(board);
                    var bits = mask;
                    var legal = true;
                    for (var i = 0; i < count; i++)
                    {
                        var type = (byte)((bits & 3) + 1);
                        bits >>= 2;
                        var sq = chosen[i];
                        if (type == Piece.WhiteMan && sq < 4 || type == Piece.BlackMan && sq >= 28)
                        {
                            legal = false;
                            break;
                        }

                        board[sq] = type;
                    }

                    if (!legal)
                        continue;
                    Add(board, true, keys, index);
                    Add(board, false, keys, index);
                }

                return;
            }

            for (var sq = next; sq < 32; sq++)
            {
                chosen[placed] = sq;
                Rec(placed + 1, sq + 1);
            }
        }

        Rec(0, 0);
    }

    static void Add(byte[] board, bool white, List<PosKey> keys, Dictionary<PosKey, int> index)
    {
        var key = new PosKey(board, white);
        if (index.ContainsKey(key))
            return;
        index.Add(key, keys.Count);
        keys.Add(key);
    }

    sealed class Layer
    {
        public Layer(Dictionary<PosKey, int> index, PosKey[] keys, byte[] state, string?[] best)
        {
            Index = index;
            Keys = keys;
            State = state;
            Best = best;
        }

        public Dictionary<PosKey, int> Index { get; }
        public PosKey[] Keys { get; }
        public byte[] State { get; }
        public string?[] Best { get; }
    }
}

readonly struct PosKey : IEquatable<PosKey>
{
    public readonly ulong Lo;
    public readonly ulong Hi;
    public readonly byte Side;

    public PosKey(byte[] sq, bool white)
    {
        ulong lo = 0;
        ulong hi = 0;
        for (var i = 0; i < 16; i++)
            lo |= (ulong)sq[i] << (i * 4);
        for (var i = 0; i < 16; i++)
            hi |= (ulong)sq[i + 16] << (i * 4);
        Lo = lo;
        Hi = hi;
        Side = (byte)(white ? 1 : 0);
    }

    public void Write(byte[] sq, out bool white)
    {
        for (var i = 0; i < 16; i++)
            sq[i] = (byte)((Lo >> (i * 4)) & 15);
        for (var i = 0; i < 16; i++)
            sq[i + 16] = (byte)((Hi >> (i * 4)) & 15);
        white = Side == 1;
    }

    public bool Equals(PosKey other) => Lo == other.Lo && Hi == other.Hi && Side == other.Side;

    public override bool Equals(object? obj) => obj is PosKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Lo, Hi, Side);
}
