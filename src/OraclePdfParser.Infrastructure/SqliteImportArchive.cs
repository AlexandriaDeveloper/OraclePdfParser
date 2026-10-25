using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;
namespace OraclePdfParser.Infrastructure;

/// <summary>
/// Local SQLite adapter. Stores verified-by-parser snapshots and issues, never source PDF bytes.
/// Reports are append-only: an earlier review is never silently overwritten by a later import.
/// </summary>
public sealed class SqliteImportArchive : IImportArchive {
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };
    public SqliteImportArchive(string filePath) {
        var full=Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=full,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Shared}.ToString();
        using var db=new SqliteConnection(_connectionString);
        db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText=@"PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS ImportReports (
 Id TEXT PRIMARY KEY NOT NULL,
 BatchId TEXT NOT NULL,
 FileName TEXT NOT NULL,
 Period TEXT,
 Kind TEXT NOT NULL,
 Status TEXT NOT NULL,
 ImportedAtUtc TEXT NOT NULL,
 EmployeeCount INTEGER NOT NULL,
 GroupCount INTEGER NOT NULL,
 IssueCount INTEGER NOT NULL,
 ReportJson TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ImportReports_Period ON ImportReports(Period, ImportedAtUtc);
CREATE INDEX IF NOT EXISTS IX_ImportReports_BatchId ON ImportReports(BatchId);";
        cmd.ExecuteNonQuery();
    }
    public async Task SaveBatchAsync(IReadOnlyList<ImportReport> reports,CancellationToken ct=default) {
        if(reports.Count==0)return;
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        var batch=Guid.NewGuid();
        var timestamp=DateTimeOffset.UtcNow.ToString("O");
        foreach(var report in reports){
            await using var cmd=db.CreateCommand();
            cmd.Transaction=(SqliteTransaction)tx;
            cmd.CommandText=@"INSERT INTO ImportReports
(Id,BatchId,FileName,Period,Kind,Status,ImportedAtUtc,EmployeeCount,GroupCount,IssueCount,ReportJson)
VALUES ($id,$batch,$name,$period,$kind,$status,$at,$employees,$groups,$issues,$json)";
            cmd.Parameters.AddWithValue("$id",report.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$batch",batch.ToString("D"));
            cmd.Parameters.AddWithValue("$name",report.FileName);
            cmd.Parameters.AddWithValue("$period",(object?)report.Period??DBNull.Value);
            cmd.Parameters.AddWithValue("$kind",report.Kind.ToString());
            cmd.Parameters.AddWithValue("$status",report.Status.ToString());
            cmd.Parameters.AddWithValue("$at",timestamp);
            cmd.Parameters.AddWithValue("$employees",report.Employees.Count);
            cmd.Parameters.AddWithValue("$groups",report.Groups.Count);
            cmd.Parameters.AddWithValue("$issues",report.Issues.Count);
            cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(report,Json));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<ImportHistory>> ListAsync(int skip,int take,CancellationToken ct=default) {
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await using var cmd=db.CreateCommand();
        cmd.CommandText=@"SELECT Id,BatchId,FileName,Period,Kind,Status,ImportedAtUtc,EmployeeCount,GroupCount,IssueCount FROM ImportReports
ORDER BY ImportedAtUtc DESC LIMIT $take OFFSET $skip";
        cmd.Parameters.AddWithValue("$take",Math.Clamp(take,1,100));
        cmd.Parameters.AddWithValue("$skip",Math.Max(0,skip));
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var results=new List<ImportHistory>();
        while(await reader.ReadAsync(ct)){
            results.Add(new ImportHistory(
                Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),
                reader.IsDBNull(3)?null:reader.GetString(3),Enum.Parse<ReportKind>(reader.GetString(4)),
                Enum.Parse<VerificationStatus>(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6),System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt32(7),reader.GetInt32(8),reader.GetInt32(9)));
        }
        return results;
    }
    public async Task<ImportReport?> GetAsync(Guid id,CancellationToken ct=default) {
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT ReportJson FROM ImportReports WHERE Id=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        var result=await cmd.ExecuteScalarAsync(ct);
        return result is string json?JsonSerializer.Deserialize<ImportReport>(json,Json):null;
    }
}