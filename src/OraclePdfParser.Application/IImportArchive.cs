using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;
public sealed record ImportHistory(Guid Id,Guid BatchId,string FileName,string? Period,ReportKind Kind,VerificationStatus Status,DateTimeOffset ImportedAtUtc,int Employees,int Groups,int Issues);
public interface IImportArchive {
 Task SaveBatchAsync(IReadOnlyList<ImportReport> reports,CancellationToken cancellationToken=default);
 Task<IReadOnlyList<ImportHistory>> ListAsync(int skip,int take,CancellationToken cancellationToken=default);
 Task<ImportReport?> GetAsync(Guid id,CancellationToken cancellationToken=default);
}