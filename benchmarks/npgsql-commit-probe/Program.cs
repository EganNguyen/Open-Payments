using System.Diagnostics;
using Npgsql;

// Throwaway probe for E1. Standbys are configured and absent, so a commit waits until released.
var admin = "Host=127.0.0.1;Port=35432;Username=postgres;Database=probe;Pooling=false";
var logPath = args[0];
var targets = new (string Name, string Conn)[]
{
    ("direct", "Host=127.0.0.1;Port=35432;Username=postgres;Database=probe"),
    ("pgbouncer", "Host=127.0.0.1;Port=36432;Username=postgres;Database=probe;No Reset On Close=true;Max Auto Prepare=0"),
};
var nextId = 0;

int Cancels() => File.ReadLines(logPath).Count(l => l.Contains("canceling wait for synchronous replication"));

async Task<string> Q(string sql)
{
    await using var c = new NpgsqlConnection(admin);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand("set synchronous_commit = local; " + sql, c);
    return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
}

async Task Standbys(bool required)
{
    var value = required ? "ANY 1 (standby1, standby2)" : "";
    await using var c = new NpgsqlConnection(admin);
    await c.OpenAsync();
    await using (var a = new NpgsqlCommand($"alter system set synchronous_standby_names = '{value}'", c)) await a.ExecuteNonQueryAsync();
    await using (var r = new NpgsqlCommand("select pg_reload_conf()", c)) await r.ExecuteNonQueryAsync();
    await Task.Delay(500);
}

async Task<bool> Visible(int id) => await Q($"select count(*) from commit_probe where id = {id}") == "1";
async Task<int> Waiting() => int.Parse(await Q("select count(*) from pg_stat_activity where wait_event = 'SyncRep'"));

// Opens a connection, inserts a row, and returns the transaction ready to commit.
async Task<(NpgsqlConnection C, NpgsqlTransaction T, int Id, List<string> Notices)> Prepare(string conn)
{
    var id = Interlocked.Increment(ref nextId);
    var c = new NpgsqlConnection(conn);
    var notices = new List<string>();
    c.Notice += (_, e) => { lock (notices) notices.Add($"{e.Notice.Severity} {e.Notice.SqlState}: {e.Notice.MessageText}"); };
    await c.OpenAsync();
    var t = await c.BeginTransactionAsync();
    await using var cmd = new NpgsqlCommand($"insert into commit_probe values ({id})", c, t);
    await cmd.ExecuteNonQueryAsync();
    return (c, t, id, notices);
}

async Task<string> Outcome(Task commit, TimeSpan wait, Stopwatch sw)
{
    try { await commit.WaitAsync(wait); return $"completed success at {sw.ElapsedMilliseconds} ms"; }
    catch (TimeoutException) { return $"still pending at {sw.ElapsedMilliseconds} ms"; }
    catch (Exception e) { return $"threw {e.GetType().Name} at {sw.ElapsedMilliseconds} ms: {e.Message.Split('\n')[0]} (inner: {e.InnerException?.GetType().Name})"; }
}

async Task DriverTimeout(string name, string conn, string settings)
{
    await Standbys(true);
    var before = Cancels();
    var p = await Prepare(conn + ";" + settings);
    var sw = Stopwatch.StartNew();
    var commit = p.T.CommitAsync();
    var result = await Outcome(commit, TimeSpan.FromSeconds(5), sw);
    await Task.Delay(300);
    var cancels = Cancels() - before;
    var visible = await Visible(p.Id);
    var waiting = await Waiting();
    Console.WriteLine($"[{name}] {settings}: {result}; cancels logged={cancels}; row visible={visible}; backends in SyncRep={waiting}; notices={string.Join(" | ", p.Notices)}");
    await Standbys(false);
    try { await commit; } catch { }
    try { await p.C.DisposeAsync(); } catch (Exception e) { Console.WriteLine($"  dispose threw {e.GetType().Name}"); }
}

async Task Parked(string name, string conn)
{
    await Standbys(true);
    var before = Cancels();
    var p = await Prepare(conn + ";Command Timeout=0");
    var sw = Stopwatch.StartNew();
    var commit = p.T.CommitAsync();
    var result = await Outcome(commit, TimeSpan.FromSeconds(1), sw);
    await Task.Delay(3000);
    Console.WriteLine($"[{name}] park: at limit {result}; at {sw.ElapsedMilliseconds} ms completed={commit.IsCompleted}; cancels logged={Cancels() - before}; row visible={await Visible(p.Id)}; backends in SyncRep={await Waiting()}");
    await Standbys(false);
    var late = await Outcome(commit, TimeSpan.FromSeconds(5), sw);
    await p.C.DisposeAsync();
    await using var again = new NpgsqlConnection(conn);
    await again.OpenAsync();
    await using var ping = new NpgsqlCommand("select 1", again);
    var reused = await ping.ExecuteScalarAsync();
    Console.WriteLine($"[{name}] park after release: {late}; cancels logged={Cancels() - before}; row visible={await Visible(p.Id)}; connection reusable={reused is 1}; notices={p.Notices.Count}");
}

async Task PoolBound(string name, string conn)
{
    await Standbys(true);
    var limited = conn + ";Command Timeout=0;Maximum Pool Size=2;Timeout=1";
    var a = await Prepare(limited);
    var b = await Prepare(limited);
    var commits = new[] { a.T.CommitAsync(), b.T.CommitAsync() };
    await Task.Delay(500);
    var sw = Stopwatch.StartNew();
    string third;
    try { await using var c = new NpgsqlConnection(limited); await c.OpenAsync(); third = "opened"; }
    catch (Exception e) { third = $"{e.GetType().Name} at {sw.ElapsedMilliseconds} ms: {e.Message.Split('\n')[0]}"; }
    Console.WriteLine($"[{name}] pool bound, 2 of 2 parked: third open -> {third}");
    await Standbys(false);
    await Task.WhenAll(commits);
    await a.C.DisposeAsync();
    await b.C.DisposeAsync();
    await using var d = new NpgsqlConnection(limited);
    await d.OpenAsync();
    Console.WriteLine($"[{name}] pool bound after release: open ok");
    NpgsqlConnection.ClearPool(d);
}

async Task ExternalCancel(string name, string conn)
{
    await Standbys(true);
    var before = Cancels();
    var p = await Prepare(conn + ";Command Timeout=0");
    var sw = Stopwatch.StartNew();
    var commit = p.T.CommitAsync();
    await Task.Delay(500);
    await Q("select pg_cancel_backend(pid) from pg_stat_activity where wait_event = 'SyncRep'");
    var result = await Outcome(commit, TimeSpan.FromSeconds(3), sw);
    await Task.Delay(100);
    Console.WriteLine($"[{name}] external cancel: {result}; cancels logged={Cancels() - before}; row visible={await Visible(p.Id)}; notices={string.Join(" | ", p.Notices)}");
    await Standbys(false);
    await p.C.DisposeAsync();
}

async Task Terminate(string name, string conn)
{
    await Standbys(true);
    var p = await Prepare(conn + ";Command Timeout=0");
    var sw = Stopwatch.StartNew();
    var commit = p.T.CommitAsync();
    await Task.Delay(500);
    await Q("select pg_terminate_backend(pid) from pg_stat_activity where wait_event = 'SyncRep'");
    var result = await Outcome(commit, TimeSpan.FromSeconds(3), sw);
    Console.WriteLine($"[{name}] backend terminated while parked: {result}; row visible={await Visible(p.Id)}; connection state={p.C.FullState}");
    await Standbys(false);
    try { await p.C.DisposeAsync(); } catch (Exception e) { Console.WriteLine($"  dispose threw {e.GetType().Name}"); }
}

foreach (var (name, conn) in targets)
{
    await DriverTimeout(name, conn, "Command Timeout=1");
    await DriverTimeout(name, conn, "Command Timeout=1;Cancellation Timeout=-1");
    await Parked(name, conn);
    await PoolBound(name, conn);
    await ExternalCancel(name, conn);
    await Terminate(name, conn);
}
await Standbys(false);
Console.WriteLine("probe finished");
