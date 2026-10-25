using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using Xunit;
namespace OraclePdfParser.Tests;
public class CrossFileReconciliationTests {
 [Fact]
 public void CanonicalNamesFromPayslipAreMatchedByIdNotLooseText() {
   const string id="00000000000000";
   var slipEmployee=new PayrollEmployee(id,"00000005-8867","اسم موظف تجريبي كامل",null,120m,40m,80m,[],[],1,2);
   var paymentEmployee=new PayrollEmployee(id,"00000005-8867","اسم موظف",null,120m,40m,80m,[],[],3,3);
   var payslip=new ImportReport(Guid.NewGuid(),"payslip.pdf",ReportKind.EmployeePayslip,VerificationStatus.Verified,2,[slipEmployee],[],[]){Period="2026-10"};
   var payment=new ImportReport(Guid.NewGuid(),"payment.pdf",ReportKind.PaymentSheet,VerificationStatus.Verified,1,[paymentEmployee],[new("بنك","فرع",1,80m,1,[])],[]){Period="2026-10"};
   var cover=new ImportReport(Guid.NewGuid(),"cover.pdf",ReportKind.Cover,VerificationStatus.Verified,1,[],[new("بنك","فرع",1,80m,1,[])],[]){Period="2026-10"};
   var result=new PayrollBatchReconciler().Reconcile([payslip,payment,cover]);
   Assert.All(result,r=>Assert.Equal(VerificationStatus.Verified,r.Status));
   var canonical=Assert.Single(result[1].Employees);
   Assert.Equal("اسم موظف تجريبي كامل",canonical.Name);
   Assert.Equal("اسم موظف",canonical.SourceName);
 }
 [Fact]
 public void WrongDisbursementNumberIsNotApproved() {
   const string id="00000000000000";
   var pay=new PayrollEmployee(id,"00000005-8868","اختبار",null,100,20,80,[],[],1,1);
   var slip=pay with {DisbursementNumber="00000005-8867"};
   var a=new ImportReport(Guid.NewGuid(),"a",ReportKind.EmployeePayslip,VerificationStatus.Verified,1,[slip],[],[]){Period="2026-10"};
   var b=new ImportReport(Guid.NewGuid(),"b",ReportKind.PaymentSheet,VerificationStatus.Verified,1,[pay],[],[]){Period="2026-10"};
   var result=new PayrollBatchReconciler().Reconcile([a,b]);
   Assert.Contains(result[1].Issues,x=>x.Code=="DISBURSEMENT_MISMATCH");
   Assert.Equal(VerificationStatus.ReviewRequired,result[1].Status);
 }
}