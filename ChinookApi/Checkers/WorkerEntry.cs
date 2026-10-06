using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChinookApi.Checkers;

public static class EngineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class WorkerRequest
{
    public string Cmd { get; set; } = "search";
    public string Position { get; set; } = "";
    public int MaxDepth { get; set; } = 8;
    public int SoftTimeMs { get; set; } = 250;
    public bool Probe { get; set; } = true;
}

public sealed class WorkerResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string BestMove { get; set; } = "";
    public string[] Pv { get; set; } = [];
    public string[] Alternatives { get; set; } = [];

    [JsonPropertyName("scoreOrWDL")]
    public int ScoreOrWdl { get; set; }
    public int Depth { get; set; }
    public long Nodes { get; set; }
    public bool TablebaseHit { get; set; }
    public string Engine { get; set; } = "chinook";
}

public static class WorkerEntry
{
    public static int Run(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = OpenReader(args, utf8);
        using var writer = OpenWriter(args, utf8);
        try
        {
            if (Arg(args, "--in").Length > 0)
                AttachConsoleForEngine();
            return Loop(args, reader, writer);
        }
        catch (Exception ex)
        {
            StartLog.Write(ex.ToString());
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new WorkerResponse { Ok = false, Error = ex.Message }, EngineJson.Options));
            }
            catch (Exception writeError)
            {
                StartLog.Write(writeError.Message);
            }

            return 1;
        }
    }

    static int Loop(string[] args, StreamReader reader, StreamWriter writer)
    {
        var enginePath = Arg(args, "--engine");
        var databases = Arg(args, "--databases");
        var tablebaseMax = int.TryParse(Arg(args, "--tb"), out var parsed) ? parsed : 4;
        StartLog.Write("user=" + Environment.UserName + " cwd=" + Environment.CurrentDirectory);
        StartLog.Write("appdata=" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var book = new EndgameBook(tablebaseMax);
        var cake = CakeDatabase.TryOpen(databases);
        var search = new SearchEngine(book, cake);
        CheckerBoardEngine? native = null;
        if (!string.IsNullOrWhiteSpace(enginePath))
        {
            native = new CheckerBoardEngine(enginePath, null);
            StartLog.Write("[worker] loaded " + enginePath);
            // KingsRow getmove under IIS often ignores playNow and overruns the PDF
            // soft/hard caps on the first (and sometimes later) midgame searches.
            // Keep the DLL loaded, but use the managed engine for normal timed levels.
            StartLog.Write("native search reserved for softTimeMs>=2000; managed search for PDF levels");
        }

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            WorkerResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<WorkerRequest>(line, EngineJson.Options);
                if (request == null || request.Cmd == "ping")
                    response = new WorkerResponse();
                else if (!Pdn.TryParse(request.Position, out var board, out var error))
                    response = new WorkerResponse { Ok = false, Error = error };
                else
                {
                    SearchResult result;
                    if (request.Probe && cake != null && cake.TrySearch(board, book, out var databaseResult))
                        result = databaseResult;
                    else if (native != null && request.SoftTimeMs >= 2000)
                        result = native.Search(board, request.MaxDepth, request.SoftTimeMs);
                    else
                        result = search.Search(board, request.MaxDepth, request.SoftTimeMs, request.Probe && board.PieceCount <= 8);
                    response = new WorkerResponse
                    {
                        BestMove = result.BestMove,
                        Pv = result.Pv,
                        Alternatives = result.Alternatives,
                        ScoreOrWdl = result.ScoreOrWdl,
                        Depth = result.Depth,
                        Nodes = result.Nodes,
                        TablebaseHit = result.TablebaseHit,
                        Engine = result.Engine
                    };
                }
            }
            catch (Exception ex)
            {
                StartLog.Write(ex.ToString());
                response = new WorkerResponse { Ok = false, Error = ex.Message };
            }

            writer.WriteLine(JsonSerializer.Serialize(response, EngineJson.Options));
        }

        native?.Dispose();
        return 0;
    }

    static StreamReader OpenReader(string[] args, Encoding encoding)
    {
        var handle = Arg(args, "--in");
        if (handle.Length > 0)
            return new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, handle), encoding);
        return new StreamReader(Console.OpenStandardInput(), encoding);
    }

    static StreamWriter OpenWriter(string[] args, Encoding encoding)
    {
        var handle = Arg(args, "--out");
        var stream = handle.Length > 0
            ? (Stream)new AnonymousPipeClientStream(PipeDirection.Out, handle)
            : Console.OpenStandardOutput();
        return new StreamWriter(stream, encoding) { AutoFlush = true };
    }

    static void AttachConsoleForEngine()
    {
        if (!OperatingSystem.IsWindows())
            return;

        FreeConsole();
        if (!AllocConsole())
            StartLog.Write("AllocConsole failed " + Marshal.GetLastWin32Error());
        else
            StartLog.Write("console attached");

        // Always give the native engine writable std handles. WriteConsole may
        // still fail without a real console, but WriteFile-style logging works.
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "kingsrow-console.log");
        var output = CreateFile(logPath, 0x40000000, 0x00000001, IntPtr.Zero, 4, 0x80, IntPtr.Zero);
        var input = CreateFile("NUL", 0x80000000, 0x00000001, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (output != new nint(-1))
        {
            SetStdHandle(-11, output);
            SetStdHandle(-12, output);
        }

        if (input != new nint(-1))
            SetStdHandle(-10, input);

        var stdout = GetStdHandle(-11);
        var consoleMode = GetConsoleMode(stdout, out var mode);
        StartLog.Write("stdout " + stdout + " consolemode " + consoleMode + " " + mode + " err " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern bool AllocConsole();

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32", SetLastError = true)]
    static extern nint GetStdHandle(int handle);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetStdHandle(int handle, nint file);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    static string Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return "";
    }
}

static class StartLog
{
    public static void Write(string message)
    {
        var line = DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine;
        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
        catch
        {
            // The service process has no console until AllocConsole runs.
        }

        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "worker-start.log"), line);
        }
        catch
        {
            // The site directory may be read-only for this identity.
        }
    }
}
