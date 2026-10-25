using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using Xunit;
public sealed class ImportTests {
 [Fact] public void ArabicNumbersAreExact()=>Assert.Equal(15449.03m,Arabic.Amount("١٥،٤٤٩٫٠٣"));
 [Fact] public void UnknownReportFailsClosed()=>Assert.Equal(VerificationStatus.Rejected,new PayrollImportService().Import("x.pdf",["test"]).Status);
 [Fact] public void PayslipRecognizesPageContinuation() {
 var pages=new[]{"نظام أوراكل للرواتب\nاسم الموظف: احمد محمد علي صالح رقم الصرف: 30200105-54865 الرقم القومي: 29001131804917\nالاستحقاقات\n21110442 بدل الجامعة ٣،٠٠٠\nاجمالي الاستحقاقات ١٥،٤٤٩٫٠٣\nالاستقطاعات\nاجمالي الاستقطاعات ٥،٥١٠٫٣٤","الصافي: ١٢،٦٠١٫٩٩"};
 var r=new PayrollImportService().Import("salary.pdf",pages);
 Assert.Equal(ReportKind.EmployeePayslip,r.Kind);
 Assert.Single(r.Employees);
 Assert.Equal(12601.99m,r.Employees[0].Net);
 Assert.Equal(2,r.Employees[0].EndPage);
 }
 [Fact] public void NoSpeculativeAssignmentOfPaymentSheetColumns(){
 var r=new PayrollImportService().Import("sheet.pdf",["كشف صرف المرتبات\nاسم الموظف والارقام"]);
 Assert.Empty(r.Employees);
 Assert.Equal(VerificationStatus.ReviewRequired,r.Status);
 }
}
