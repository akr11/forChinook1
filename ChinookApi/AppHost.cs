using System.Diagnostics;
using System.Text.Json;
using ChinookApi.Checkers;

namespace ChinookApi;

public static class AppHost
{
    public static WebApplication Create(string[] args, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = ContentRoot()
        });
        if (extra != null)
            builder.Configuration.AddInMemoryCollection(extra);

        var engineOptions = new EngineOptions();
        var cacheOptions = new CacheOptions();
        var limitOptions = new LimitOptions();
        builder.Configuration.GetSection("Engine").Bind(engineOptions);
        builder.Configuration.GetSection("Cache").Bind(cacheOptions);
        builder.Configuration.GetSection("Limits").Bind(limitOptions);
        if (engineOptions.Workers <= 0)
            engineOptions.Workers = 2;
        if (engineOptions.TablebaseMaxPieces <= 0)
            engineOptions.TablebaseMaxPieces = 4;
        var databases = ResolveDatabases(engineOptions.Databases, builder.Environment.ContentRootPath);

        var inProcess = engineOptions.Mode.Equals("inprocess", StringComparison.OrdinalIgnoreCase);
        if (inProcess)
        {
            builder.Services.AddSingleton(sp => new InProcessPool(engineOptions.Workers, engineOptions.TablebaseMaxPieces, databases));
            builder.Services.AddSingleton<ICheckerPool>(sp => sp.GetRequiredService<InProcessPool>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<InProcessPool>());
        }
        else
        {
            builder.Services.AddSingleton(sp => new WorkerPool(
                sp.GetRequiredService<ILogger<WorkerPool>>(),
                engineOptions.Path,
                databases,
                engineOptions.Workers,
                engineOptions.TablebaseMaxPieces));
            builder.Services.AddSingleton<ICheckerPool>(sp => sp.GetRequiredService<WorkerPool>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerPool>());
        }

        var app = builder.Build();
        var cache = new MoveCache(cacheOptions.Capacity, cacheOptions.TtlMinutes);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ChinookApi");
        if (!string.IsNullOrEmpty(app.Environment.WebRootPath) && Directory.Exists(app.Environment.WebRootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.MapGet("/healthz", (ICheckerPool pool) =>
        {
            var alive = pool.Alive;
            return Results.Json(new { ok = alive > 0, workers = alive });
        });

        app.MapPost("/v1/moves", (MovesRequest? request) =>
        {
            if (!Pdn.TryParse(request?.Position, out var board, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var moves = Rules.LegalMoves(board).Select(move => move.ToPdn()).ToArray();
            return Results.Json(new MovesResponse { Side = board.WhiteToMove ? "W" : "B", Moves = moves });
        });

        app.MapPost("/v1/move/validate", (ValidateRequest? request) =>
        {
            if (!Pdn.TryParse(request?.Position, out var board, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (request?.Move == null || !Rules.TryMatch(board, request.Move, out var move, out _) || move == null)
                return Results.Json(new ValidateResponse { Legal = false });
            board.Make(move);
            return Results.Json(new ValidateResponse { Legal = true, Position = board.ToPdn() });
        });

        app.MapPost("/v1/move/suggest", async (SuggestRequest? request, ICheckerPool pool, CancellationToken cancellationToken) =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            var clock = Stopwatch.StartNew();
            if (request?.GameId != null && !request.GameId.Equals("checkers-8x8", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "gameId must be checkers-8x8" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (request?.State?.Notation != null && !request.State.Notation.Equals("PDN", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "notation must be PDN" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (!Pdn.TryParse(request?.State?.Position, out var board, out var error))
                return Results.Json(new { error }, statusCode: StatusCodes.Status422UnprocessableEntity);

            if (!TryLimits(request?.Level, request?.Limits, limitOptions, out var depth, out var soft, out var hard, out var levelError))
                return Results.Json(new { error = levelError }, statusCode: StatusCodes.Status422UnprocessableEntity);

            var canonical = board.ToPdn();
            var cacheKey = $"{canonical}|{depth}|{soft}|{hard}";
            if (cache.TryGet(cacheKey, out var cached) && cached != null)
            {
                cached.Info.TimeMs = (int)clock.ElapsedMilliseconds;
                Log(logger, requestId, cached);
                return Results.Json(cached);
            }

            try
            {
                var found = await pool.SearchAsync(new WorkerRequest
                {
                    Cmd = "search",
                    Position = canonical,
                    MaxDepth = depth,
                    SoftTimeMs = soft,
                    Probe = true
                }, hard, cancellationToken);

                if (!found.Ok)
                    return Results.Json(new { error = found.Error ?? "Engine error" }, statusCode: StatusCodes.Status500InternalServerError);

                var legal = Rules.LegalMoves(board);
                var names = legal.Select(move => move.ToPdn()).ToHashSet(StringComparer.Ordinal);
                string? chosen = null;
                if (legal.Count == 0)
                    chosen = "";
                else if (names.Contains(found.BestMove))
                    chosen = found.BestMove;
                else
                {
                    foreach (var alternative in found.Alternatives.Concat(found.Pv))
                    {
                        if (!names.Contains(alternative))
                            continue;
                        chosen = alternative;
                        break;
                    }
                }

                if (chosen == null)
                    return Results.Json(new { error = "Engine returned no legal move" }, statusCode: StatusCodes.Status500InternalServerError);

                var response = new SuggestResponse
                {
                    Engine = string.IsNullOrEmpty(found.Engine) ? "chinook" : found.Engine,
                    BestMove = chosen,
                    Pv = found.Pv.Length == 0 && chosen.Length > 0 ? [chosen] : found.Pv,
                    ScoreOrWdl = found.ScoreOrWdl,
                    Depth = found.Depth,
                    Nodes = found.Nodes,
                    PositionKey = "pdn:" + canonical,
                    Info = new SuggestInfo
                    {
                        TablebaseHit = found.TablebaseHit,
                        TimeMs = (int)clock.ElapsedMilliseconds,
                        Backend = found.TablebaseHit && !string.IsNullOrEmpty(databases)
                            ? "chinook"
                            : string.IsNullOrWhiteSpace(engineOptions.Path) ? "builtin" : "kingsrow"
                    }
                };
                cache.Set(cacheKey, response);
                Log(logger, requestId, response);
                return Results.Json(response);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "Search timed out" }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        return app;
    }

    static void Log(ILogger logger, string requestId, SuggestResponse response)
    {
        logger.LogInformation("{Payload}", JsonSerializer.Serialize(new
        {
            requestId,
            timeMs = response.Info.TimeMs,
            depth = response.Depth,
            nodes = response.Nodes,
            tablebaseHit = response.Info.TablebaseHit
        }));
    }

    static bool TryLimits(string? level, SearchLimits? requested, LimitOptions defaults, out int depth, out int soft, out int hard, out string error)
    {
        var name = string.IsNullOrWhiteSpace(level) ? "medium" : level.Trim().ToLowerInvariant();
        error = "";
        switch (name)
        {
            case "weak":
                depth = 8;
                soft = 100;
                break;
            case "medium":
                depth = 12;
                soft = 250;
                break;
            case "strong":
                depth = 18;
                soft = 500;
                break;
            default:
                depth = 0;
                soft = 0;
                hard = 0;
                error = "level must be weak, medium, or strong";
                return false;
        }

        if (requested?.MaxDepth is > 0)
            depth = Math.Clamp(requested.MaxDepth.Value, 1, 32);
        if (requested?.SoftTimeMs is > 0)
            soft = Math.Clamp(requested.SoftTimeMs.Value, 1, 30_000);
        hard = requested?.HardTimeMs is > 0
            ? Math.Clamp(requested.HardTimeMs.Value, 1, 30_000)
            : Math.Max(defaults.DefaultHardTimeMs, soft);
        if (hard < soft)
            hard = soft;
        return true;
    }

    static string ResolveDatabases(string configured, string contentRoot)
    {
        var relative = string.IsNullOrWhiteSpace(configured) ? "db" : configured.Trim();
        if (Path.IsPathRooted(relative))
            return Directory.Exists(relative) ? relative : "";
        var fromContent = Path.GetFullPath(Path.Combine(contentRoot, relative));
        if (Directory.Exists(fromContent))
            return fromContent;
        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        return Directory.Exists(fromBase) ? fromBase : "";
    }

    static string ContentRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChinookApi.csproj")))
                return dir.FullName;
        }

        return AppContext.BaseDirectory;
    }
}
