using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();
app.UseStaticFiles();

// ==================== /health ====================
app.MapGet("/health", () =>
    Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// ==================== /version ====================
app.MapGet("/version", (IConfiguration cfg) =>
    Results.Ok(new
    {
        name = cfg["App:Name"] ?? "IsLabApp",
        version = cfg["App:Version"] ?? "unknown"
    }));

// ==================== /db/ping ====================
app.MapGet("/db/ping", (IConfiguration cfg) =>
{
    var cs = cfg.GetConnectionString("Mssql");
    if (string.IsNullOrEmpty(cs))
        return Results.Ok(new { status = "error", message = "Connection string not configured" });
    try
    {
        using var conn = new SqlConnection(cs);
        conn.Open();
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = "error", message = ex.Message });
    }
});

// ==================== /api/notes CRUD ====================
var notes = new ConcurrentDictionary<int, Note>();
int nextId = 0;

app.MapGet("/api/notes", () => notes.Values.OrderBy(n => n.Id).ToList());

app.MapGet("/api/notes/{id}", (int id) =>
    notes.TryGetValue(id, out var n) ? Results.Ok(n) : Results.NotFound("Note not found"));

app.MapPost("/api/notes", (NoteDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Title))
        return Results.BadRequest(new { error = "Title is required" });
    if (dto.Title.Length > 200)
        return Results.BadRequest(new { error = "Title too long (max 200)" });

    var id = Interlocked.Increment(ref nextId);
    var note = new Note(id, dto.Title.Trim(), dto.Text?.Trim() ?? "", DateTime.UtcNow);
    notes[id] = note;
    return Results.Created($"/api/notes/{id}", note);
});

app.MapDelete("/api/notes/{id}", (int id) =>
    notes.TryRemove(id, out _) ? Results.Ok(new { deleted = id }) : Results.NotFound());

// ==================== SignalR Hubs ====================
app.MapHub<TicTacToeHub>("/hubs/tictactoe");
app.MapHub<ArenaHub>("/hubs/arena");

app.Run();

// ==================== Models ====================
record Note(int Id, string Title, string Text, DateTime CreatedAt);
record NoteDto(string Title, string? Text);

// ==================== Крестики-нолики: Hub ====================
public class TicTacToeRoom
{
    public string? PlayerX, PlayerO;
    public string[] Board = Enumerable.Repeat("", 9).ToArray();
    public string Turn = "X";
    public bool Over;
}

public class TicTacToeHub : Hub
{
    static readonly ConcurrentDictionary<string, TicTacToeRoom> Rooms = new();

    public async Task Join(string room)
    {
        var r = Rooms.GetOrAdd(room, _ => new TicTacToeRoom());
        await Groups.AddToGroupAsync(Context.ConnectionId, room);

        string sym;
        if (r.PlayerX == null) { r.PlayerX = Context.ConnectionId; sym = "X"; }
        else if (r.PlayerO == null) { r.PlayerO = Context.ConnectionId; sym = "O"; }
        else { await Clients.Caller.SendAsync("Err", "Комната заполнена"); return; }

        await Clients.Caller.SendAsync("Role", sym);
        if (r.PlayerX != null && r.PlayerO != null)
            await Clients.Group(room).SendAsync("Go", "X");
    }

    public async Task Move(string room, int cell)
    {
        if (!Rooms.TryGetValue(room, out var r) || r.Over) return;
        bool isX = Context.ConnectionId == r.PlayerX;
        string sym = isX ? "X" : "O";
        if (r.Turn != sym || cell < 0 || cell > 8 || r.Board[cell] != "") return;

        r.Board[cell] = sym;
        r.Turn = sym == "X" ? "O" : "X";
        await Clients.Group(room).SendAsync("Moved", cell, sym);

        var w = Win(r.Board);
        if (w != null) { r.Over = true; await Clients.Group(room).SendAsync("End", w); }
        else if (r.Board.All(c => c != "")) { r.Over = true; await Clients.Group(room).SendAsync("End", "draw"); }
    }

    public async Task Reset(string room)
    {
        if (Rooms.TryGetValue(room, out var r))
        {
            r.Board = Enumerable.Repeat("", 9).ToArray();
            r.Turn = "X"; r.Over = false;
            await Clients.Group(room).SendAsync("Rst");
            await Clients.Group(room).SendAsync("Go", "X");
        }
    }

    string? Win(string[] b)
    {
        int[][] l = { [0, 1, 2], [3, 4, 5], [6, 7, 8], [0, 3, 6], [1, 4, 7], [2, 5, 8], [0, 4, 8], [2, 4, 6] };
        foreach (var s in l)
            if (b[s[0]] != "" && b[s[0]] == b[s[1]] && b[s[1]] == b[s[2]]) return b[s[0]];
        return null;
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        foreach (var kv in Rooms)
        {
            var r = kv.Value;
            if (r.PlayerX == Context.ConnectionId || r.PlayerO == Context.ConnectionId)
            {
                await Clients.Group(kv.Key).SendAsync("End", "disconnect");
                Rooms.TryRemove(kv.Key, out _);
            }
        }
        await base.OnDisconnectedAsync(ex);
    }
}

// ==================== Арена (вид сверху, WASD): Hub ====================
public class Player { public double X { get; set; } public double Y { get; set; } public string C { get; set; } = ""; public string N { get; set; } = ""; }

public class ArenaHub : Hub
{
    static readonly ConcurrentDictionary<string, Player> Players = new();
    static readonly string[] Cols = ["#e74c3c", "#3498db", "#2ecc71", "#f1c40f", "#9b59b6", "#e67e22", "#1abc9c", "#ff6b81"];
    static int ci;

    public async Task Enter(string name)
    {
        var c = Cols[Interlocked.Increment(ref ci) % Cols.Length];
        var rnd = new Random();
        Players[Context.ConnectionId] = new Player { X = rnd.Next(50, 750), Y = rnd.Next(50, 550), C = c, N = name };
        await SendAll();
    }

    public async Task Move(string dir)
    {
        if (!Players.TryGetValue(Context.ConnectionId, out var p)) return;
        int sp = 6;
        switch (dir)
        {
            case "w": p.Y = Math.Max(10, p.Y - sp); break;
            case "s": p.Y = Math.Min(590, p.Y + sp); break;
            case "a": p.X = Math.Max(10, p.X - sp); break;
            case "d": p.X = Math.Min(790, p.X + sp); break;
        }
        await SendAll();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        Players.TryRemove(Context.ConnectionId, out _);
        await SendAll();
        await base.OnDisconnectedAsync(ex);
    }

    async Task SendAll() =>
        await Clients.All.SendAsync("State", Players.Values.ToList());
}