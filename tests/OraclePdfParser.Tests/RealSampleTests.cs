using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;
using Xunit;

namespace OraclePdfParser.Tests;

/// <summary>Requires privately held original PDFs. NEVER upload personal payroll documents to a public repository.</summary>
public sealed class RealSampleTests
{
    [Fact]
    public async Task Optional_PrivatePdfCorpus_ReconcilesKnownTotals()
    {
        var directory=Environment.GetEnvironmentVariable("ORACLE_PDF_SAMPLE_DIR");
        if(string.IsNullOrWhiteSpace(directory)) return; // Opt-in regression test with local sensitive documents only.
        var paths=new[]{"صرفيه موظف(1).pdf","كافر.pdf","كشوف صرف.pdf"};
        Assert.All(paths,name=>Assert.True(File.Exists(Path.Combine(directory,name)),name));
        var files=paths.Select(name=>new UploadSource(name,File.ReadAllBytes(Path.Combine(directory,name)))).ToArray();
        var repository=new MemoryRepository();
        var batch=await new PayrollImportService(new PdfiumTextExtractor(),repository).ImportAsync(files);
        Assert.Equal(219,batch.Employees.Count);
        Assert.Equal(56,batch.Covers.Count);
        Assert.Equal(2_864_042.24m,batch.NetFromPayslips);
        Assert.Equal(2_864_042.24m,batch.NetFromCovers);
        Assert.Equal(219,batch.Payments.Count);
        Assert.Equal(2_864_042.24m,batch.NetFromPayments);
        Assert.False(batch.HasBlockingErrors,string.Join("\n",batch.Issues.Select(i=>i.Code)));
    }
    private sealed class MemoryRepository:IPayrollImportRepository
    {
        public Task SaveAsync(PayrollImport value,CancellationToken ct)=>Task.CompletedTask;
        public Task<PayrollImport?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<PayrollImport?>(null);
        public Task<IReadOnlyList<PayrollImport>> ListAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<PayrollImport>>([]);
    }
}
