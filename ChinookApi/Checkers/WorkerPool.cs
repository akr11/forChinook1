using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ChinookApi.Checkers;

public interface ICheckerPool
{
    int Alive { get; }
    Task<WorkerResponse> SearchAsync(WorkerRequest request, int hardTimeMs, CancellationToken cancellationToken);
}

public sealed class WorkerPool : ICheckerPool, IHostedService, IAsyncDisposable
{
    readonly List<EngineProcess> _workers = [];
    readonly ILogger<WorkerPool> _logger;
    readonly string _enginePath;
    readonly string _databases;
    readonly int _tablebaseMax;
    int _cursor;

    public WorkerPool(ILogger<WorkerPool> logger, string enginePath, string databases, int workers, int tablebaseMax)
    {
        _logger = logger;
        _enginePath = enginePath;
        _databases = databases;
        _tablebaseMax = tablebaseMax;
        Count = Math.Clamp(workers, 1, 8);
    }

    public int Count { get; }
    public int Alive => _workers.Count(worker => worker.Alive);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < Count; i++)
        {
            var worker = new EngineProcess(_enginePath, _databases, _tablebaseMax, _logger);
            try
            {
                await worker.StartAsync(cancellationToken);
                _workers.Add(worker);
            }
            catch (Exception ex)
            {
                Trace("worker " + i + " failed: " + ex);
                _logger.LogError(ex, "Checker worker {Index} failed to start", i);
                worker.Kill();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var worker in _workers)
            worker.Kill();
        return Task.CompletedTask;
    }

    public async Task<WorkerResponse> SearchAsync(WorkerRequest request, int hardTimeMs, CancellationToken cancellationToken)
    {
        if (_workers.Count == 0)
            throw new InvalidOperationException("No checker workers are running");

        var index = Interlocked.Increment(ref _cursor);
        var worker = _workers[(int)((uint)index % (uint)_workers.Count)];
        await worker.Gate.WaitAsync(cancellationToken);
        var sent = false;
        try
        {
            sent = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(1, hardTimeMs));
            return await worker.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (sent)
        {
            await worker.RestartAsync();
            throw;
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
            worker.Kill();
        await Task.CompletedTask;
    }

    static string DotnetHost()
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? "", "dotnet", "dotnet.exe"),
                @"C:\Program Files\dotnet\dotnet.exe"
            };
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
        }

        return "dotnet";
    }

    static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "worker-start.log"),
                DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    sealed class EngineProcess
    {
        readonly string _enginePath;
        readonly string _databases;
        readonly int _tablebaseMax;
        readonly ILogger _logger;
        Process? _process;
        StreamWriter? _input;
        StreamReader? _output;
        AnonymousPipeServerStream? _commands;
        AnonymousPipeServerStream? _replies;

        public EngineProcess(string enginePath, string databases, int tablebaseMax, ILogger logger)
        {
            _enginePath = enginePath;
            _databases = databases;
            _tablebaseMax = tablebaseMax;
            _logger = logger;
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Alive => _process is { HasExited: false };

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var dll = typeof(WorkerEntry).Assembly.Location;
            var host = DotnetHost();
            Trace("starting " + host);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _commands = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            _replies = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var start = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!string.IsNullOrWhiteSpace(_enginePath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_enginePath));
                if (!string.IsNullOrEmpty(directory))
                    start.WorkingDirectory = directory;
            }

            var profile = Path.Combine(AppContext.BaseDirectory, "logs", "profile");
            try
            {
                Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming"));
                Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local"));
                start.Environment["USERPROFILE"] = profile;
                start.Environment["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
                start.Environment["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
                start.Environment["TEMP"] = Path.Combine(profile, "AppData", "Local");
                start.Environment["TMP"] = Path.Combine(profile, "AppData", "Local");
            }
            catch (Exception ex)
            {
                Trace("profile folder: " + ex.Message);
            }

            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("--worker");
            start.ArgumentList.Add("--in");
            start.ArgumentList.Add(_commands.GetClientHandleAsString());
            start.ArgumentList.Add("--out");
            start.ArgumentList.Add(_replies.GetClientHandleAsString());
            start.ArgumentList.Add("--tb");
            start.ArgumentList.Add(_tablebaseMax.ToString());
            if (!string.IsNullOrWhiteSpace(_enginePath))
            {
                start.ArgumentList.Add("--engine");
                start.ArgumentList.Add(_enginePath);
            }

            if (!string.IsNullOrWhiteSpace(_databases))
            {
                start.ArgumentList.Add("--databases");
                start.ArgumentList.Add(_databases);
            }

            _process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start dotnet worker");
            _commands.DisposeLocalCopyOfClientHandle();
            _replies.DisposeLocalCopyOfClientHandle();
            _input = new StreamWriter(_commands, utf8) { AutoFlush = true };
            _output = new StreamReader(_replies, utf8);

            var ping = await SendAsync(new WorkerRequest { Cmd = "ping" }, cancellationToken);
            if (!ping.Ok)
                throw new InvalidOperationException(ping.Error ?? "Worker ping failed");
        }

        public async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (_input == null || _output == null)
                throw new InvalidOperationException("Worker is not running");
            await _input.WriteLineAsync(JsonSerializer.Serialize(request, EngineJson.Options).AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            var line = await _output.ReadLineAsync(cancellationToken);
            if (line == null)
                throw new InvalidOperationException("Worker closed the pipe");
            return JsonSerializer.Deserialize<WorkerResponse>(line, EngineJson.Options)
                   ?? throw new InvalidOperationException("Empty worker response");
        }

        public async Task RestartAsync()
        {
            Kill();
            await StartAsync(CancellationToken.None);
        }

        public void Kill()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Worker process already stopped");
            }

            try
            {
                _input?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Worker input already closed");
            }

            try
            {
                _output?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Worker output already closed");
            }

            _process?.Dispose();
            _process = null;
            _input = null;
            _output = null;
            _commands = null;
            _replies = null;
        }
    }
}

public sealed class InProcessPool : ICheckerPool, IHostedService
{
    readonly SearchEngine[] _engines;
    readonly SemaphoreSlim[] _gates;
    int _cursor;

    public InProcessPool(int workers, int tablebaseMax, string? databases = null)
    {
        var count = Math.Clamp(workers, 1, 8);
        var cake = CakeDatabase.TryOpen(databases);
        _engines = new SearchEngine[count];
        _gates = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++)
        {
            _engines[i] = new SearchEngine(new EndgameBook(tablebaseMax), cake);
            _gates[i] = new SemaphoreSlim(1, 1);
        }
    }

    public int Alive => _engines.Length;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<WorkerResponse> SearchAsync(WorkerRequest request, int hardTimeMs, CancellationToken cancellationToken)
    {
        var index = (int)((uint)Interlocked.Increment(ref _cursor) % (uint)_engines.Length);
        await _gates[index].WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(1, hardTimeMs));
            var result = await Task.Run(() =>
            {
                if (!Pdn.TryParse(request.Position, out var board, out var error))
                    return new WorkerResponse { Ok = false, Error = error };
                var found = _engines[index].Search(board, request.MaxDepth, request.SoftTimeMs, request.Probe && board.PieceCount <= 8);
                return new WorkerResponse
                {
                    BestMove = found.BestMove,
                    Pv = found.Pv,
                    Alternatives = found.Alternatives,
                    ScoreOrWdl = found.ScoreOrWdl,
                    Depth = found.Depth,
                    Nodes = found.Nodes,
                    TablebaseHit = found.TablebaseHit,
                    Engine = found.Engine
                };
            }, timeout.Token);
            return result;
        }
        finally
        {
            _gates[index].Release();
        }
    }
}
