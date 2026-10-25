using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using Xunit;
namespace OraclePdfParser.Tests;

public sealed class OracleEdgeCaseTests {
 [Fact]
 public void IgnoresSubtotalXAndReadsNonstandardDeductionAccount(){
   const string sample="نظام أوراكل للرواتب\nاسم الموظف: موظف اختباري غير حقيقي رقم الصرف: 00000000-00001 الرقم القومى: 00000000000001\nالاستحقاقات\n21110324 حافز جودة 3,700\nxحافز جودة 4,200\n21110101 المرتب الاساسى 500\nأجمالى الاستحقاقات 4,200\nالاستقطاعات\n41110104 ضريبة تجريبية 99.50\nعمولة تحصيل 0.50 11320129\nالأجمالى الاستقطاعات 100.00\nالصافى: 4,100";
   var r=new PayrollImportService().Import("fixture.pdf",[sample]);var e=Assert.Single(r.Employees);
   Assert.Equal(4200m,e.Entries.Where(x=>x.Kind==EntryKind.Earning).Sum(x=>x.Amount));
   Assert.Equal(100m,e.Entries.Where(x=>x.Kind==EntryKind.Deduction).Sum(x=>x.Amount));
   Assert.Contains(e.Entries,x=>x.AccountCode=="11320129" && x.Amount==.50m);
   Assert.DoesNotContain(e.Entries,x=>x.Kind==EntryKind.Earning && x.Amount==4200m);
 }
 [Fact]
 public void CrossPagePaymentRowIsReconstructed(){
   var first=string.Join(" ",new[]{"120.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","120.00","100.00"});
   var second=string.Join(" ",new[]{"20.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00"});
   var pages=new[]{
      "كشف صرف المرتبات\nالاستقطاعات\n1\n8867-000000\nموظف اختباري محمد سالم "+first+" 05",
      "كشف صرف المرتبات\nالاستقطاعات\n000000000000\n02 "+second+"\nإجمالى السجل\n100.00\nإقرار"
   };
   var r=new PayrollImportService().Import("split.pdf",pages);
   var e=Assert.Single(r.Employees);
   Assert.Equal(100m,e.Net);
   Assert.Equal("00000000000002",e.NationalId);
   Assert.Equal("00000005-8867",e.DisbursementNumber);
   Assert.Equal(100m,Assert.Single(r.Groups).TotalNet);
 }
 [Fact]
 public void RecoversOnlyMatchingSplitFractionalGroupTotal(){
   var first=string.Join(" ",new[]{"16000.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","16000.00","12800.00"});
   var second=string.Join(" ",new[]{"3200.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00"});
   var input="كشف صرف المرتبات\nالاستقطاعات\n1\n8867-000000\nموظف تجريبي محمد علي "+first+" 05\n000000000000\n17 "+second+
       "\nإجمالى السجل\n3200.0\n0\n0.00 0.00 0.00\n12800.0\n0\nإقرار";
   var r=new PayrollImportService().Import("split-totals.pdf",[input]);
   Assert.Equal(12800m,Assert.Single(r.Groups).TotalNet);
   Assert.DoesNotContain(r.Issues,x=>x.Code=="PAYMENT_GROUP_REVIEW");
 }
 [Fact]
 public void AmountsBeforeNameAndStandalonePaymentPrefixAreResolvedByContent(){
   var first=string.Join(" ",new[]{"12800.00","16000.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00"});
   var second=string.Join(" ",new[]{"3200.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00","0.00"});
   var doc="كشف صرف المرتبات\nالاستقطاعات\n1\n8867-000000\n05\nموظف تجريبي محمد كامل\n"+first+
      " الشريف\n000000000000\n17 "+second+"\nإجمالى السجل\n12800.00\nإقرار";
   var imported=new PayrollImportService().Import("names.pdf",[doc]);
   var employee=Assert.Single(imported.Employees);
   Assert.Equal("موظف تجريبي محمد كامل الشريف",employee.Name);
   Assert.Equal("00000005-8867",employee.DisbursementNumber);
   Assert.Equal("00000000000017",employee.NationalId);
   Assert.Equal(16000m,employee.Gross);
   Assert.Equal(12800m,employee.Net);
   Assert.Equal(VerificationStatus.Verified,imported.Status);
 }

}