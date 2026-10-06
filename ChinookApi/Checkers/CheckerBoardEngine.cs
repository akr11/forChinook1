using System.Runtime.InteropServices;
using System.Text;

namespace ChinookApi.Checkers;

public sealed class CheckerBoardEngine : IDisposable
{
    readonly NativeCommand? _command;
    readonly NativeGetMove _getMove;
    readonly nint _library;
    bool _disposed;

    public CheckerBoardEngine(string dllPath, string? databases)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A CheckerBoard engine DLL runs only on Windows.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(dllPath)) ?? ".";
        var parent = Directory.GetParent(directory)?.FullName;
        StartLog.Write("dll directory " + directory);
        SetDllDirectory(directory);
        foreach (var folder in new[] { directory, parent })
        {
            if (string.IsNullOrEmpty(folder))
                continue;
            var companion = Path.Combine(folder, "egdb64.dll");
            if (!File.Exists(companion))
                continue;
            StartLog.Write("load " + companion);
            NativeLibrary.Load(companion);
            StartLog.Write("loaded " + companion);
            break;
        }

        StartLog.Write("load " + dllPath);
        _library = NativeLibrary.Load(dllPath);
        StartLog.Write("loaded " + dllPath);
        _getMove = Marshal.GetDelegateForFunctionPointer<NativeGetMove>(NativeLibrary.GetExport(_library, "getmove"));
        var commandExport = NativeLibrary.GetExport(_library, "enginecommand");
        _command = commandExport == 0
            ? null
            : Marshal.GetDelegateForFunctionPointer<NativeCommand>(commandExport);
        // enginecommand talks to the Windows console. IIS workers have none, and
        // AllocConsole is denied in session 0, so skip setup commands here.
        // KingsRow English defaults to draughts type 21; Cake DB covers endgames.
        StartLog.Write("engine ready");
    }

    public SearchResult Search(BoardState board, int maxDepth, int softTimeMs)
    {
        var squares = new int[64];
        for (var number = 1; number <= 32; number++)
        {
            var piece = board.Sq[number - 1];
            if (piece == Piece.Empty)
                continue;
            NumberToCoors(number, out var x, out var y);
            squares[x * 8 + y] = ToCheckerBoard(piece);
        }

        var status = new byte[1024];
        var playNow = new int[1];
        var soft = Math.Max(1, softTimeMs);
        var abort = new Thread(() =>
        {
            Thread.Sleep(soft);
            Volatile.Write(ref playNow[0], 1);
        })
        {
            IsBackground = true
        };
        abort.Start();
        CbMove move;
        int code;
        unsafe
        {
            fixed (int* boardPtr = squares)
            fixed (int* playPtr = playNow)
            fixed (byte* statusPtr = status)
            {
                move = default;
                // info=0: normal timed search; playNow aborts when the timer fires.
                code = _getMove(boardPtr, board.WhiteToMove ? 1 : 2, soft / 1000.0, statusPtr, playPtr, 0, 0, &move);
            }
        }

        var text = Encoding.ASCII.GetString(status).TrimEnd('\0', ' ', '\r', '\n');
        var pdn = ToPdn(ref move);
        var tablebase = board.PieceCount <= 8 &&
                        (text.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("tablebase", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("wld", StringComparison.OrdinalIgnoreCase));

        return new SearchResult
        {
            BestMove = pdn,
            Pv = string.IsNullOrEmpty(pdn) ? [] : [pdn],
            Alternatives = string.IsNullOrEmpty(pdn) ? [] : [pdn],
            ScoreOrWdl = code switch
            {
                1 => 1,
                2 => -1,
                0 => 0,
                _ => 0
            },
            Depth = maxDepth,
            Nodes = 0,
            TablebaseHit = tablebase,
            Engine = "chinook"
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeLibrary.Free(_library);
    }

    void Command(string command)
    {
        if (_command == null)
            return;
        var bytes = Encoding.ASCII.GetBytes(command + "\0");
        var reply = new byte[1024];
        unsafe
        {
            fixed (byte* commandPtr = bytes)
            fixed (byte* replyPtr = reply)
                _command(commandPtr, replyPtr);
        }
    }

    static int ToCheckerBoard(byte piece) => piece switch
    {
        Piece.WhiteMan => 5,
        Piece.WhiteKing => 9,
        Piece.BlackMan => 6,
        Piece.BlackKing => 10,
        _ => 0
    };

    static void NumberToCoors(int number, out int x, out int y)
    {
        number--;
        y = number / 4;
        x = 2 * (3 - number % 4);
        if ((y & 1) != 0)
            x++;
    }

    static int CoorsToNumber(int x, int y) => 4 * (y + 1) - x / 2;

    static unsafe string ToPdn(ref CbMove move)
    {
        var from = CoorsToNumber(move.FromX, move.FromY);
        var to = CoorsToNumber(move.ToX, move.ToY);
        if (from is < 1 or > 32 || to is < 1 or > 32)
            return "";
        if (move.Jumps <= 0)
            return $"{from}-{to}";

        var parts = new List<int> { from };
        var jumps = Math.Min(move.Jumps, 11);
        for (var i = 1; i <= jumps; i++)
        {
            var square = CoorsToNumber(move.Path[i * 2], move.Path[i * 2 + 1]);
            if (square is < 1 or > 32 || square == parts[^1])
                continue;
            parts.Add(square);
        }

        if (parts[^1] != to)
            parts.Add(to);
        return string.Join('x', parts);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    unsafe delegate int NativeGetMove(int* board, int color, double maxTime, byte* status, int* playNow, int info, int moreInfo, CbMove* move);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    unsafe delegate int NativeCommand(byte* command, byte* reply);

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct CbMove
    {
        public int Jumps;
        public int NewPiece;
        public int OldPiece;
        public int FromX;
        public int FromY;
        public int ToX;
        public int ToY;
        public fixed int Path[24];
        public fixed int Del[24];
        public fixed int DelPiece[12];
    }
}
