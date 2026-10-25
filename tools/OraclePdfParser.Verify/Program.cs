using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;
using System.Globalization;
var paths=args.Where(x=>!x.StartsWith("--")).ToArray();
bool strict=args.Contains("--strict");
bool gold=args.Contains("--golden-october-2026");
if(paths.Length is <1 or >3 || paths.Any(p=>!File.Exists(p))){
 Console.Error.WriteLine("Usage: dotnet run --project tools/OraclePdfParser.Verify -- [--strict] [--golden-october-2026] <file1.pdf> [file2.pdf] [file3.pdf]");
 return 2;
}
var source=new PdfiumTextSource();var parser=new PayrollImportService();
var reports=new List<ImportReport>();
foreach(var path in paths){
 using var file=File.OpenRead(path);
 reports.Add(parser.Import(Path.GetFileName(path),source.ReadPages(file)));
}
var checkedReports=new PayrollBatchReconciler().Reconcile(reports);
foreach(var r in checkedReports){
 var net=r.Employees.Where(x=>x.Net is not null).Sum(x=>x.Net!.Value);
 Console.WriteLine($"{r.Kind}: {r.Pages} pages, {r.Employees.Count} employees, {r.Groups.Count} groups, net from employee rows {net.ToString("0.00",CultureInfo.InvariantCulture)}, {r.Issues.Count} issues, {r.Status}, period {r.Period ?? "UNKNOWN"}");
 foreach(var issue in r.Issues.Take(15)) Console.Error.WriteLine($" {issue.Code}: {issue.Message} (page {issue.Page})");
}
if(gold){
 var slips=checkedReports.FirstOrDefault(x=>x.Kind==ReportKind.EmployeePayslip);
 var cover=checkedReports.FirstOrDefault(x=>x.Kind==ReportKind.Cover);
 var payment=checkedReports.FirstOrDefault(x=>x.Kind==ReportKind.PaymentSheet);
 if(slips is null||cover is null||payment is null){Console.Error.WriteLine("All 3 Oracle October report types are required");return 1;}
 var total=slips.Employees.Where(x=>x.Net is not null).Sum(x=>x.Net!.Value);
 var cTotal=cover.Groups.Where(x=>x.TotalNet is not null).Sum(x=>x.TotalNet!.Value);
 var count=cover.Groups.Where(x=>x.EmployeeCount is not null).Sum(x=>x.EmployeeCount!.Value);
 if(slips.Employees.Count!=219||payment.Employees.Count!=219||cover.Groups.Count!=56||payment.Groups.Count!=56||count!=219||total!=2864042.24m||cTotal!=2864042.24m){
    Console.Error.WriteLine("FAIL: uploaded reports did not match privately validated October 2026 control totals");
    return 1;
 }
 Console.WriteLine("October 2026 reference totals and group counts match");
}
if(strict && checkedReports.Any(x=>x.Status!=VerificationStatus.Verified)){Console.Error.WriteLine("FAIL: strict verification requires zero issues");return 1;}
return 0;