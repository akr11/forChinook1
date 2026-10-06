using System.Text.Json.Serialization;

namespace ChinookApi.Checkers;

public sealed class SuggestRequest
{
    public string? GameId { get; set; }
    public PositionState? State { get; set; }
    public string? Level { get; set; }
    public SearchLimits? Limits { get; set; }
}

public sealed class PositionState
{
    public string? Notation { get; set; }
    public string? Position { get; set; }
}

public sealed class SearchLimits
{
    public int? MaxDepth { get; set; }
    public int? SoftTimeMs { get; set; }
    public int? HardTimeMs { get; set; }
}

public sealed class SuggestResponse
{
    public string Engine { get; set; } = "chinook";
    public string? BestMove { get; set; }
    public string[] Pv { get; set; } = [];

    [JsonPropertyName("scoreOrWDL")]
    public int ScoreOrWdl { get; set; }
    public int Depth { get; set; }
    public long Nodes { get; set; }
    public string PositionKey { get; set; } = "";
    public SuggestInfo Info { get; set; } = new();
}

public sealed class SuggestInfo
{
    public bool TablebaseHit { get; set; }
    public int TimeMs { get; set; }
    public string Backend { get; set; } = "builtin";
}

public sealed class ValidateRequest
{
    public string? Position { get; set; }
    public string? Move { get; set; }
}

public sealed class ValidateResponse
{
    public bool Legal { get; set; }
    public string? Position { get; set; }
}

public sealed class MovesRequest
{
    public string? Position { get; set; }
}

public sealed class MovesResponse
{
    public string Side { get; set; } = "B";
    public string[] Moves { get; set; } = [];
}

public sealed class EngineOptions
{
    public string Type { get; set; } = "chinook";
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "process";
    public int Workers { get; set; } = 2;
    public string Databases { get; set; } = "";
    public int TablebaseMaxPieces { get; set; } = 4;
}

public sealed class CacheOptions
{
    public int Capacity { get; set; } = 20000;
    public int TtlMinutes { get; set; } = 15;
}

public sealed class LimitOptions
{
    public int DefaultSoftTimeMs { get; set; } = 300;
    public int DefaultHardTimeMs { get; set; } = 1200;
}
