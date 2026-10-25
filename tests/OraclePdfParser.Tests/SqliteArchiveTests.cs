using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
namespace OraclePdfParser.Tests;
public class SqliteArchiveTests {
 [Fact]
 public async Task ArchivesBatchAndReloadsExactFinancialEntries() {
   var folder=Path.Combine(Path.GetTempPath(),"oracle-parser-tests",Guid.NewGuid().ToString("N"));
   try {
     var archive=new SqliteImportArchive(Path.Combine(folder,"samples.db"));
     var record=new PayrollEntry("21110101","المرتب الاساسى",1250.25m,EntryKind.Earning,2,"21110101 المرتب الاساسى 1250.25");
     var employee=new PayrollEmployee("00000000000000","00000000-00000","موظف تجريبي",null,1250.25m,50m,1200.25m,[record],[],1,2);
     var report=new ImportReport(Guid.NewGuid(),"synthetic.pdf",ReportKind.EmployeePayslip,VerificationStatus.ReviewRequired,2,[employee],[],[new("UNCERTAIN","synthetic warning",2)]){Period="2026-10"};
     await archive.SaveBatchAsync([report]);
     var summaries=await archive.ListAsync(0,20);
     var summary=Assert.Single(summaries);
     Assert.Equal(report.Id,summary.Id);
     Assert.Equal(1,summary.Employees);
     var recovered=await archive.GetAsync(report.Id);
     Assert.NotNull(recovered);
     Assert.Equal("2026-10",recovered.Period);
     Assert.Equal(1200.25m,Assert.Single(recovered.Employees).Net);
     Assert.Equal(1250.25m,Assert.Single(recovered.Employees[0].Entries).Amount);
     Assert.Equal(VerificationStatus.ReviewRequired,recovered.Status);
   } finally {
     SqliteConnection.ClearAllPools();
     if(Directory.Exists(folder)) Directory.Delete(folder,true);
   }
 }
}