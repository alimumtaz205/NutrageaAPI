using MySqlConnector;
using Nutragea.Api.Services;
using System.Data;
using System.Globalization;
namespace Nutragea.Api.Data;
public sealed class Db(IConfiguration config)
{
    public async Task<MySqlConnection> Open(CancellationToken ct=default)
    {
        var cs=config.GetConnectionString("MySql");
        if(string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Configure ConnectionStrings:MySql using user-secrets or environment variables.");
        var c=new MySqlConnection(cs);
        try { await c.OpenAsync(ct); return c; } catch { await c.DisposeAsync(); throw; }
    }
    public async Task<T> Write<T>(Func<Sql,Task<T>> action,CancellationToken ct=default)
    {
        await using var c=await Open(ct); await using var tx=await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var sql=new Sql(c,tx,ct);
        // One short global inventory lock gives deterministic ordering for this single-company MVP.
        // All business writes take this lock, including quality changes and master-data edits.
        await sql.Rows("SELECT id FROM mutation_guard WHERE id=1 FOR UPDATE");
        var result=await action(sql); await tx.CommitAsync(ct); return result;
    }
    public async Task<List<Dictionary<string,object?>>> Query(string text,object? args=null,CancellationToken ct=default)
    { await using var c=await Open(ct); return await new Sql(c,null,ct).Rows(text,args); }
}
public sealed class Sql(MySqlConnection connection,MySqlTransaction? transaction,CancellationToken ct)
{
    MySqlCommand Command(string text,object? args)
    {
        var cmd=new MySqlCommand(text,connection,transaction);
        if(args!=null) foreach(var p in args.GetType().GetProperties()) cmd.Parameters.AddWithValue("@"+p.Name,p.GetValue(args)??DBNull.Value);
        return cmd;
    }
    public async Task<int> ExecParameters(string text,Dictionary<string,object?> args)
    {
        await using var cmd=new MySqlCommand(text,connection,transaction);
        foreach(var pair in args) cmd.Parameters.AddWithValue("@"+pair.Key,pair.Value??DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<int> Exec(string text,object? args=null)
    { await using var cmd=Command(text,args); return await cmd.ExecuteNonQueryAsync(ct); }
    public async Task<long> Insert(string text,object? args=null)
    { await using var cmd=Command(text,args); await cmd.ExecuteNonQueryAsync(ct); return cmd.LastInsertedId; }
    public async Task<List<Dictionary<string,object?>>> Rows(string text,object? args=null)
    {
        await using var cmd=Command(text,args); await using var reader=await cmd.ExecuteReaderAsync(ct);
        var result=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(ct)) { var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase); for(int i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=await reader.IsDBNullAsync(i,ct)?null:reader.GetValue(i); result.Add(row); }
        return result;
    }
    public async Task<Dictionary<string,object?>> One(string text,object? args=null)
    { var rows=await Rows(text,args); if(rows.Count==0) throw new ApiException(404,"Record not found."); return rows[0]; }
    public async Task Audit(long actor,string action,string entity,long id,object? details=null)
    { await Exec("INSERT INTO audit_logs(actor_id,action,entity,entity_id,details,created_at) VALUES(@actor,@action,@entity,@id,@details,UTC_TIMESTAMP(6))",new{actor,action,entity,id,details=System.Text.Json.JsonSerializer.Serialize(details??new{})}); }
}
public static class Row
{
 public static long L(this Dictionary<string,object?> r,string k)=>Convert.ToInt64(r[k],CultureInfo.InvariantCulture);
 public static decimal D(this Dictionary<string,object?> r,string k)=>Convert.ToDecimal(r[k],CultureInfo.InvariantCulture);
 public static string S(this Dictionary<string,object?> r,string k)=>Convert.ToString(r[k],CultureInfo.InvariantCulture)??"";
 public static bool B(this Dictionary<string,object?> r,string k)=>Convert.ToBoolean(r[k],CultureInfo.InvariantCulture);
}
