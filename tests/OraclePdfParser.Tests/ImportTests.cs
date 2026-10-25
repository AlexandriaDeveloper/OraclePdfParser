using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using Xunit;

public sealed class ImportTests {
 [Theory]
 [InlineData("١٥،٤٤٩٫٠٣","15449.03")]
 [InlineData("۵۶٫۴۰","56.40")]
 [InlineData("2,500.00","2500.00")]
 public void ArabicMoney(string input,string result)=>Assert.Equal(decimal.Parse(result,System.Globalization.CultureInfo.InvariantCulture),Arabic.Amount(input));
 [Fact] public void UnknownReportFailsClosed()=>Assert.Equal(VerificationStatus.Rejected,new PayrollImportService().Import("x.pdf",["test"]).Status);
 [Fact] public void PayslipRecognizesPageContinuationAndGovernmentShares(){
  var pages=new[]{
   "نظام أوراكل للرواتب\nاسم الموظف: موظف تجريبي محمد علي رقم الصرف: 00000000-00000 الرقم القومى: 00000000000000\nالبنك: بنك مصر الفرع: الاسكندرية\nالاستحقاقات\n21110442 بدل الجامعة ١٠٠٫٠٠\nاجمالي الاستحقاقات ١٠٠٫٠٠\n21120202 حكومة ١ إصابة عمل ٥٫٠٠\n21120202 حكومة 0.25% إصابة عمل ٥٫٠٠\n21120201 حكومة ٣ تأمين صحي ٥٫٠٠\nالاستقطاعات\n41110201 مؤمن ٩ ٤٠٫٠٠\nاجمالي الاستقطاعات ٤٠٫٠٠",
   "نظام أوراكل للرواتب\n21120101 حكومة ١٢ شيخوخة ٥٫٠٠\nالصافى: ٨٠٫٠٠"
  };
  var r=new PayrollImportService().Import("salary.pdf",pages);
  Assert.Equal(ReportKind.EmployeePayslip,r.Kind);
  Assert.Single(r.Employees);
  Assert.Equal("موظف تجريبي محمد علي",r.Employees[0].Name);
  Assert.Equal(80m,r.Employees[0].Net);
  Assert.Equal(2,r.Employees[0].EndPage);
  Assert.Contains(r.Employees[0].Entries,e=>e.Kind==EntryKind.Earning && e.Amount==100m);
  Assert.Contains(r.Employees[0].Entries,e=>e.Kind==EntryKind.Deduction && e.Amount==40m);
 }
 [Fact] public void CoverGroupsCanSpanPages(){
  var r=new PayrollImportService().Import("cover.pdf",[
  "وزارة المالية\nكشف إجماليات المرتب لكل سجل وبنك عن شهر أكتوبر 2026\nالبنك : بنك مصر الفرع: مركز البطاقات\nالاستحقاقات",
  "جملة الصافى المستحق ٢٤،٧٨٩٫٢٨\nعدد العاملين: ٢"
  ]);
  Assert.Single(r.Groups);
  Assert.Equal(2,r.Groups[0].EmployeeCount);
  Assert.Equal(24789.28m,r.Groups[0].TotalNet);
 }
 [Fact] public void PaymentSheetReconcilesSafelyWithoutCoordinates(){
  var first=string.Join(" ",new[]{"100.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","100.00","80.00"});
  var second=string.Join(" ",new[]{"20.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00"});
  var txt="وزارة المالية\nكشف صرف المرتبات\nالاستقطاعات\nأخرى\nجملة\nالاستقطاعات\n1\n8867-000000\nموظف تجريبي محمد علي "+first+" 05\n000000000000\n17 "+second+"\nإجمالى السجل\n80.00\nإقرار";
  var r=new PayrollImportService().Import("payments.pdf",[txt]);
  Assert.Equal(ReportKind.PaymentSheet,r.Kind);
  Assert.Single(r.Employees);
  Assert.Equal(80m,r.Employees[0].Net);
  Assert.Equal("00000000000017",r.Employees[0].NationalId);
  Assert.Equal("00000005-8867",r.Employees[0].DisbursementNumber);
  Assert.Single(r.Groups);
  Assert.Equal(80m,r.Groups[0].TotalNet);
 }
 [Fact] public void ReconcilerFlagsGroupNetDifference(){
    var a=new ImportReport(Guid.NewGuid(),"a.pdf",ReportKind.Cover,VerificationStatus.Verified,1,[],[new("Bank","Branch",1,100,1,[])],[]);
    var b=new ImportReport(Guid.NewGuid(),"b.pdf",ReportKind.PaymentSheet,VerificationStatus.Verified,1,[],[new("Bank","Branch",1,99,1,[])],[]);
    var result=new PayrollBatchReconciler().Reconcile([a,b]);
    Assert.All(result,r=>Assert.Equal(VerificationStatus.ReviewRequired,r.Status));
    Assert.Contains(result[0].Issues,i=>i.Code=="GROUP_NET");
 }
 [Fact] public void IncompleteCoverCannotBeMarkedVerified(){
   var r=new PayrollImportService().Import("cover.pdf",[
     "وزارة المالية\nكشف إجماليات المرتب لكل سجل وبنك عن شهر أكتوبر 2026\nالاستحقاقات\nالبنك : بنك مصر الفرع: مركز البطاقات\nجملة الصافى المستحق 100.00\nعدد العاملين: 1",
     "الاستحقاقات\nالبنك : بنك مصر الفرع: مركز البطاقات\nاستقطاعات بدون نهاية المجموعة"
   ]);
   Assert.Equal(VerificationStatus.ReviewRequired,r.Status);
   Assert.Contains(r.Issues,i=>i.Code=="INCOMPLETE_COVER_GROUPS");
 }

 [Fact] public void RecognizesArabicPayrollMonthAcrossReportTypes(){
   var report=new PayrollImportService().Import("test.pdf",[
     "كشف إجماليات المرتب لكل سجل وبنك عن شهر: أكتوبر سنــــة ٢٠٢٦\nالاستحقاقات\nالبنك: بنك مصر الفرع: مركز البطاقات\nجملة الصافى المستحق 100.00\nعدد العاملين: 1"
   ]);
   Assert.Equal("2026-10",report.Period);
 }

}