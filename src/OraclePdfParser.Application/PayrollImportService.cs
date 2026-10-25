using System.Text.RegularExpressions;
using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;

/// <summary>Content-based, fail-closed parser. No X/Y coordinates, fixed page indices, or positional columns.</summary>
public sealed class PayrollImportService : IPayrollImportService {
    private static readonly Regex NationalId = new(@"(?<!\d)\d{14}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PaymentId = new(@"(?<!\d)\d{5,10}-\d{4,8}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex AccountCode = new(@"(?<!\d)(?:211|411)\d{5}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(?<!\d)\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?(?!\d)|(?<!\d)\d+(?:\.\d{1,2})?(?!\d)",RegexOptions.Compiled);
    public ImportReport Import(string fileName, IReadOnlyList<string> pages) {
        var normalized=pages.Select(Arabic.Normalize).ToArray();
        var joined=string.Join("\n", normalized.Select(x => x[..Math.Min(x.Length,1500)]));
        var key=Arabic.Key(joined);
        var kind=key.Contains("كشف صرف المرتبات")?ReportKind.PaymentSheet:
            key.Contains("كشف اجماليات المرتب")?ReportKind.Cover:
            key.Contains("اسم الموظف")&&key.Contains("الاستحقاقات")?ReportKind.EmployeePayslip:ReportKind.Unknown;
        return kind switch {
            ReportKind.EmployeePayslip=>ParsePayslips(fileName,normalized),
            ReportKind.Cover=>ParseCover(fileName,normalized),
            ReportKind.PaymentSheet=>ParsePayments(fileName,normalized),
            _=>new ImportReport(Guid.NewGuid(),fileName,kind,VerificationStatus.Rejected,pages.Count,[],[],[new("UNKNOWN_REPORT","Unrecognized Oracle report; no data was approved.",null,"error")])
        };
    }
    private static bool Has(string text,string phrase)=>Arabic.Key(text).Contains(Arabic.Key(phrase));
    private static decimal? ValueAfter(string text,string marker) {
        foreach(var line in Arabic.Lines(text)) {
            if(!Has(line,marker))continue;
            var m=Regex.Match(Arabic.Normalize(line),@"(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?\s*$");
            if(m.Success) return Arabic.Amount(m.Value.Trim());
        }
        return null;
    }
    private ImportReport ParsePayslips(string file,IReadOnlyList<string> pages) {
        var groups=new List<(int start,List<string> text)>();
        for(var i=0;i<pages.Count;i++) {
            var p=pages[i]; var isStart=Has(p,"اسم الموظف") && NationalId.IsMatch(p);
            if(isStart) groups.Add((i+1,new List<string>()));
            if(groups.Count>0) groups[^1].text.Add(p);
        }
        var employees=new List<PayrollEmployee>(); var issues=new List<ImportIssue>();
        if(groups.Count==0) issues.Add(new("NO_EMPLOYEES","No employee headers could be identified.",null,"error"));
        foreach(var g in groups) {
            var t=string.Join("\n",g.text); var lines=Arabic.Lines(t); var id=NationalId.Match(t).Value;
            var pay=PaymentId.Match(t).Value;
            var name=ExtractBetween(t,"اسم الموظف", "رقم الصرف");
            if(string.IsNullOrWhiteSpace(name)) {
                var start=Array.FindIndex(lines,l=>Has(l,"اسم الموظف")); 
                if(start>=0) name=Regex.Replace(lines[start],@"^.*?اسم الموظف\s*:?\s*","").Trim();
            }
            var gross=ValueAfter(t,"اجمالي الاستحقاقات")??ValueAfter(t,"أجمالي الاستحقاقات");
            var deductions=ValueAfter(t,"اجمالي الاستقطاعات")??ValueAfter(t,"الأجمالى الاستقطاعات");
            var net=ValueAfter(t,"الصافي")??ValueAfter(t,"الصافى");
            var bank=ExtractBetween(t,"البنك","الفرع");
            var warnings=new List<string>();
            if(id.Length!=14)warnings.Add("Missing national ID");
            if(string.IsNullOrWhiteSpace(name))warnings.Add("Missing employee name");
            if(net is null)warnings.Add("Missing net payment");
            // Gross/deduction totals can include non-cash government items: no unsafe accounting inference.
            if(gross is null || deductions is null)warnings.Add("Missing reported gross or deductions");
            var entries=ParseEntries(lines,g.start,warnings);
            if(entries.Count==0)warnings.Add("No reliably associated financial detail entries");
            var end=g.start+g.text.Count-1;
            employees.Add(new(id.Length==14?id:null,string.IsNullOrEmpty(pay)?null:pay,name??"",bank,gross,deductions,net,entries,warnings,g.start,end));
            foreach(var w in warnings)issues.Add(new("EMPLOYEE_REVIEW",$"Employee {id}: {w}",g.start));
        }
        return new(Guid.NewGuid(),file,ReportKind.EmployeePayslip,issues.Count>0?VerificationStatus.ReviewRequired:VerificationStatus.Verified,pages.Count,employees,[],issues);
    }
    private static string? ExtractBetween(string text,string begin,string end) {
        // Only use labeled textual boundaries; mixed bidi streams may not preserve them.
        var m=Regex.Match(Arabic.Normalize(text),Regex.Escape(begin)+@"\s*:?\s*(.*?)\s*"+Regex.Escape(end),RegexOptions.Singleline);
        if(!m.Success)return null;
        var candidate=Regex.Replace(m.Groups[1].Value,@"\s+"," ").Trim(' ',':');
        return candidate.Length is >0 and <160 ?candidate:null;
    }
    private static List<PayrollEntry> ParseEntries(string[] lines,int startPage,List<string> warnings) {
        var result=new List<PayrollEntry>();
        var section=EntryKind.Informational;
        var buffer=new List<string>();string? code=null;
        void Flush(){
            if(buffer.Count==0)return;
            var raw=string.Join(" ",buffer);
            // Never infer amounts from a multi-number line (years, percentages, account numbers).
            var numberLines=buffer.SelectMany(x=>Regex.Matches(x,@"(?<!\d)\d+(?:,\d{3})*(?:\.\d{1,2})?(?!\d)").Select(m=>m.Value)).ToList();
            if(section is EntryKind.Earning or EntryKind.Deduction && numberLines.Count==1 && Arabic.Amount(numberLines[0]) is {} amount) {
                var label=Regex.Replace(raw,@"(?<!\d)\d+(?:,\d{3})*(?:\.\d{1,2})?(?!\d)","").Trim();
                if(!string.IsNullOrWhiteSpace(label))result.Add(new PayrollEntry(code,label,amount,section,startPage,raw));
            } else if(section is EntryKind.Earning or EntryKind.Deduction) warnings.Add("Ambiguous item: "+raw[..Math.Min(80,raw.Length)]);
            buffer.Clear();
        }
        foreach(var line in lines) {
            if(Has(line,"الاستحقاقات") && !Has(line,"اجمالي")&&!Has(line,"اجمالى")) { Flush();section=EntryKind.Earning;continue; }
            if(Has(line,"الاستقطاعات") && !Has(line,"اجمالي")&&!Has(line,"اجمالى")) { Flush();section=EntryKind.Deduction;continue; }
            if(Has(line,"الصافي")||Has(line,"الصافى")) {Flush();section=EntryKind.Informational;continue;}
            if(section==EntryKind.Informational)continue;
            if(Has(line,"الاسم")&&Has(line,"القيمة"))continue;
            if(Has(line,"اجمالي")||Has(line,"اجمالى")||Has(line,"الأجمالى")){Flush();continue;}
            var c=AccountCode.Match(line);
            if(c.Success){Flush();code=c.Value; var remainder=line.Replace(c.Value,"").Trim(); if(remainder.Length>0)buffer.Add(remainder);continue;}
            if(Regex.IsMatch(line,@"^\s*x\s",RegexOptions.IgnoreCase)){Flush();code=null;continue;}
            if(!string.IsNullOrWhiteSpace(line))buffer.Add(line);
        }
        Flush();
        return result;
    }
    private ImportReport ParseCover(string file,IReadOnlyList<string> pages) {
        var groups=new List<CoverGroup>();var issues=new List<ImportIssue>();
        string? bank=null,branch=null;int start=1;
        for(var i=0;i<pages.Count;i++) {
            var text=pages[i];
            var b=ExtractBetween(text,"البنك","الفرع");
            if(!string.IsNullOrWhiteSpace(b)) {bank=b;start=i+1;}
            var f=Regex.Match(text,@"الفرع\s*:\s*([^\r\n]+)");
            if(f.Success)branch=f.Groups[1].Value.Trim();
            if(!Has(text,"جملة الصافي المستحق"))continue;
            var net=ValueAfter(text,"جملة الصافي المستحق");
            var count=Regex.Match(text,@"عدد العاملين\s*:\s*(\d+)");
            var n=count.Success?int.Parse(count.Groups[1].Value): (int?)null;
            var warn=new List<string>();
            if(net is null)warn.Add("Could not reliably read cover net");
            if(n is null)warn.Add("Could not reliably read employee count");
            groups.Add(new(bank,branch,n,net,start,warn));
            foreach(var w in warn)issues.Add(new("COVER_REVIEW",w,i+1));
        }
        if(groups.Count==0)issues.Add(new("NO_COVER_GROUPS","No recognizable cover totals",null,"error"));
        return new(Guid.NewGuid(),file,ReportKind.Cover,VerificationStatus.ReviewRequired,pages.Count,[],groups,issues);
    }
    private ImportReport ParsePayments(string file,IReadOnlyList<string> pages) {
        // Multi-column payment sheets require independent layout validation. Never guess identities from flattened lines.
        var issues=new List<ImportIssue>{new("PAYMENT_LAYOUT_REVIEW","Payment sheet detected. Individual multi-column amounts cannot safely be assigned using plain text order; manual review required.",null)};
        return new(Guid.NewGuid(),file,ReportKind.PaymentSheet,VerificationStatus.ReviewRequired,pages.Count,[],[],issues);
    }
}
