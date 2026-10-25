using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;
/// <summary>Cross-file financial checks. Never synthesizes missing payroll data.</summary>
public sealed class PayrollBatchReconciler {
 public IReadOnlyList<ImportReport> Reconcile(IReadOnlyList<ImportReport> reports) {
   var slips=reports.FirstOrDefault(r=>r.Kind==ReportKind.EmployeePayslip);
   var sheet=reports.FirstOrDefault(r=>r.Kind==ReportKind.PaymentSheet);
   var cover=reports.FirstOrDefault(r=>r.Kind==ReportKind.Cover);
   var extra=reports.ToDictionary(r=>r.Id,_=>new List<ImportIssue>());
   void Add(ImportReport r,string code,string message)=>extra[r.Id].Add(new(code,message,null,"error"));
   if(reports.Count>1) {
      foreach(var dup in reports.GroupBy(r=>r.Kind).Where(g=>g.Count()>1))
        foreach(var r in dup)Add(r,"DUPLICATE_DOCUMENT_TYPE","More than one file of the same report type; cannot automatically select source");
      var periods=reports.Where(r=>r.Period is not null).Select(r=>r.Period).Distinct().ToArray();
      if(periods.Length>1)foreach(var r in reports)Add(r,"PAYROLL_PERIOD_MISMATCH","Uploaded reports belong to different months");
      if(reports.Any(r=>r.Period is null))foreach(var r in reports.Where(r=>r.Period is null))Add(r,"PAYROLL_PERIOD_UNKNOWN","Cannot confirm report month for reconciliation");
   }
   if(slips is not null&&sheet is not null) {
      var ids=sheet.Employees.Where(e=>e.NationalId is not null).GroupBy(e=>e.NationalId).ToDictionary(g=>g.Key!,g=>g.ToArray());
      foreach(var e in slips.Employees){
        if(e.NationalId is null||!ids.TryGetValue(e.NationalId,out var m)){Add(sheet,"MISSING_EMPLOYEE","Payslip employee not found in payment sheet");continue;}
        if(m.Length!=1){Add(sheet,"DUPLICATE_EMPLOYEE","Duplicate payment employee");continue;}
        if(e.Net is not null&&m[0].Net is not null&&Math.Abs(e.Net.Value-m[0].Net.Value)>.02m)
          Add(sheet,"NET_MISMATCH","Employee net differs between payslip and payment sheet");
      }
      if(slips.Employees.Count!=sheet.Employees.Count){
         Add(slips,"COUNT_MISMATCH","Payslip and payment sheet employee counts differ");
         Add(sheet,"COUNT_MISMATCH","Payslip and payment sheet employee counts differ");
      }
   }
   if(cover is not null&&sheet is not null){
      if(cover.Groups.Count!=sheet.Groups.Count){Add(cover,"GROUP_COUNT","Group counts differ");Add(sheet,"GROUP_COUNT","Group counts differ");}
      for(int i=0;i<Math.Min(cover.Groups.Count,sheet.Groups.Count);i++){
          var a=cover.Groups[i];var b=sheet.Groups[i];
          if(a.TotalNet is not null&&b.TotalNet is not null&&Math.Abs(a.TotalNet.Value-b.TotalNet.Value)>.02m){
             Add(cover,"GROUP_NET",$"Group {i+1} total mismatch");Add(sheet,"GROUP_NET",$"Group {i+1} total mismatch");
          }
          if(a.EmployeeCount is not null&&b.EmployeeCount is not null&&a.EmployeeCount.Value!=b.EmployeeCount.Value){
             Add(cover,"GROUP_EMPLOYEES",$"Group {i+1} count mismatch");Add(sheet,"GROUP_EMPLOYEES",$"Group {i+1} count mismatch");
          }
      }
   }
   if(cover is not null&&slips is not null){
      var people=cover.Groups.Where(g=>g.EmployeeCount is not null).Sum(g=>g.EmployeeCount!.Value);
      var amount=cover.Groups.Where(g=>g.TotalNet is not null).Sum(g=>g.TotalNet!.Value);
      var slipsAmount=slips.Employees.Where(e=>e.Net is not null).Sum(e=>e.Net!.Value);
      if(people!=slips.Employees.Count){Add(cover,"EMPLOYEE_TOTAL","Total employee count mismatch");Add(slips,"EMPLOYEE_TOTAL","Total employee count mismatch");}
      if(Math.Abs(amount-slipsAmount)>.02m){Add(cover,"NET_TOTAL","Total net mismatch");Add(slips,"NET_TOTAL","Total net mismatch");}
   }
   return reports.Select(r=>{var issues=r.Issues.Concat(extra[r.Id]).ToArray();return r with{Issues=issues,Status=issues.Length>0?VerificationStatus.ReviewRequired:r.Status};}).ToArray();
 }
}