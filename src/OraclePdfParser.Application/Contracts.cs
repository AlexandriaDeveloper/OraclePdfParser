using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;
public interface IPdfTextExtractor { PdfDocumentText Extract(byte[] file); }
public interface IPayrollImportRepository
{
    Task SaveAsync(PayrollImport value, CancellationToken cancellationToken);
    Task<PayrollImport?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollImport>> ListAsync(CancellationToken cancellationToken);
}
public sealed record UploadSource(string Name, byte[] Bytes);
