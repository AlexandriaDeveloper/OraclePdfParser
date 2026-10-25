using System.Text.Json;
using Microsoft.Data.Sqlite;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;

namespace OraclePdfParser.Infrastructure;

public sealed class SqliteImportRepository : IPayrollImportRepository
{
    private readonly string _connectionString;
    public SqliteImportRepository(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate}.ToString();
        using var db=new SqliteConnection(_connectionString);
        db.Open();
        using var command=db.CreateCommand();
        command.CommandText=@"CREATE TABLE IF NOT EXISTS import_batches
        (id TEXT PRIMARY KEY, created_at TEXT NOT NULL, contents TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public async Task SaveAsync(PayrollImport value,CancellationToken cancellationToken)
    {
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var command=db.CreateCommand();
        command.CommandText="INSERT INTO import_batches (id,created_at,contents) VALUES ($id,$created,$contents)";
        command.Parameters.AddWithValue("$id",value.Id.ToString());
        command.Parameters.AddWithValue("$created",value.ImportedAt.ToString("O"));
        command.Parameters.AddWithValue("$contents",JsonSerializer.Serialize(value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<PayrollImport?> GetAsync(Guid id,CancellationToken cancellationToken)
    {
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT contents FROM import_batches WHERE id=$id";
        command.Parameters.AddWithValue("$id",id.ToString());
        var contents=await command.ExecuteScalarAsync(cancellationToken) as string;
        return contents is null?null:JsonSerializer.Deserialize<PayrollImport>(contents);
    }
    public async Task<IReadOnlyList<PayrollImport>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT contents FROM import_batches ORDER BY created_at DESC LIMIT 100";
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        var entries=new List<PayrollImport>();
        while(await reader.ReadAsync(cancellationToken))
        {
            var record=JsonSerializer.Deserialize<PayrollImport>(reader.GetString(0));
            if(record!=null) entries.Add(record);
        }
        return entries;
    }
}