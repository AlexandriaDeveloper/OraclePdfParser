using System.Globalization;
using System.Text.RegularExpressions;
using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;

/// <summary>
/// Semantic PDF text-stream parser. Never uses X/Y positions, fixed page numbers,
/// or a predetermined number of employees. Incomplete/ambiguous records fail closed.
/// </summary>
public sealed class PayrollImportService : IPayrollImportService {
    private static readonly Regex NationalId = new(@"(?<!\d)\d{14}(?!\d)",RegexOptions.Compiled);
    private static readonly Regex Account = new(@"(?<!\d)(?:211|411)\d{5}(?!\d)",RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(?<!\d)\d+(?:,\d{3})*(?:\.\d{1,2})?(?!\d)",RegexOptions.Compiled);
    private static readonly Regex Currency = new(@"(?<!\d)\d+(?:,\d{3})*\.\d{2}(?!\d)",RegexOptions.Compiled);
    private static readonly Regex ReversePay = new(@"(?<!\d)(\d{4,6})-(\d{5,6})(?!\d)",RegexOptions.Compiled);
    private static readonly Regex Pay = new(@"(?<!\d)\d{7,9}-\d{4,6}(?!\d)",RegexOptions.Compiled);
    private static readonly StringComparison Ignore=StringComparison.OrdinalIgnoreCase;
    private static string K(string? s)=>Arabic.Key(s);
    private static bool Has(string? t,string token)=>K(t).Contains(K(token),Ignore);
    private static bool GrossLine(string l)=>Has(l,"اجمالي الاستحقاقات") || Has(l,"أجمالى الاستحقاقات");
    private static bool DeductionLine(string l)=>Has(l,"اجمالي الاستقطاعات") || Has(l,"الأجمالى الاستقطاعات");
    private static bool NetLine(string l)=>Has(l,"الصافي:") || Has(l,"الصافى:");
    private static decimal? LastAmount(string l){
        var tokens=Currency.Matches(l).Select(m=>m.Value).ToArray();
        // Allow integer totals but ignore solitary year in a heading.
        if(tokens.Length==0) tokens=Money.Matches(l).Select(m=>m.Value).ToArray();
        return tokens.Length==0?null:Arabic.Amount(tokens[^1]);
    }
    private static string? Labeled(string text,string begin,string end) {
        var m=Regex.Match(text,Regex.Escape(begin)+@"\s*:\s*(.*?)\s*"+Regex.Escape(end),RegexOptions.Singleline);
        if(!m.Success)return null;
        var v=Regex.Replace(m.Groups[1].Value,@"\s+"," ").Trim(' ',':');
        return v.Length is >0 and <130?v:null;
    }
    private static string? Field(string text,string label) {
        var m=Regex.Match(text,Regex.Escape(label)+@"\s*:\s*([^\n]+)");
        return m.Success?m.Groups[1].Value.Trim():null;
    }
    public ImportReport Import(string fileName,IReadOnlyList<string> pages) {
        var clean=pages.Select(Arabic.Normalize).ToArray();
        var sample=K(string.Join("\n",clean.Take(4)));
        var kind=sample.Contains("كشف صرف المرتبات")?ReportKind.PaymentSheet:
            sample.Contains("كشف اجماليات المرتب")?ReportKind.Cover:
            sample.Contains("اسم الموظف")&&sample.Contains("الاستحقاقات")?ReportKind.EmployeePayslip:
            ReportKind.Unknown;
        return kind switch {
            ReportKind.EmployeePayslip => Payslips(fileName,clean),
            ReportKind.Cover => Cover(fileName,clean),
            ReportKind.PaymentSheet => Payment(fileName,clean),
            _=>new(Guid.NewGuid(),fileName,kind,VerificationStatus.Rejected,pages.Count,[],[],[new("UNKNOWN_REPORT","Unknown report structure",null,"error")])
        };
    }
    private ImportReport Payslips(string file,IReadOnlyList<string> pages) {
        var records=new List<(int Start,List<(int Number,string Text)> Pages)>();
        for(int i=0;i<pages.Count;i++) {
            if(Has(pages[i],"اسم الموظف")&&NationalId.IsMatch(pages[i]))
                records.Add((i+1,new()));
            if(records.Count>0) records[^1].Pages.Add((i+1,pages[i]));
        }
        var employees=new List<PayrollEmployee>();var issues=new List<ImportIssue>();
        if(records.Count==0)issues.Add(new("NO_EMPLOYEE_HEADERS","No employee headers were found",null,"error"));
        foreach(var r in records){
            var all=string.Join("\n",r.Pages.Select(p=>p.Text));var header=r.Pages[0].Text;
            var id=NationalId.Match(header).Value;
            var pay=Pay.Match(header).Value;
            var name=Labeled(header,"اسم الموظف","رقم الصرف") ?? Field(header,"اسم الموظف") ?? "";
            // PDF text streams can interleave digits and labels: isolate the name before payment marker.
            name=Regex.Replace(name,@"\s+"," ").Trim();
            var bank=Labeled(header,"البنك","الفرع");
            var warnings=new List<string>();
            if(id.Length!=14)warnings.Add("Missing 14-digit national ID");
            if(pay.Length==0)warnings.Add("Missing payment identifier");
            if(name.Length<5 || name.Any(char.IsDigit)) warnings.Add("Ambiguous employee name");
            var entries=ExtractEntries(r.Pages,warnings);
            decimal? gross=null,deduct=null,net=null;
            foreach(var line in Arabic.Lines(all)){
                if(GrossLine(line))gross=LastAmount(line);
                if(DeductionLine(line))deduct=LastAmount(line);
                if(NetLine(line))net=LastAmount(line);
            }
            if(gross is null || deduct is null || net is null)
                warnings.Add("Missing official payslip gross, deductions or net");
            else {
                // Oracle employer insurance is included in deductions but not in payslip cash gross.
                // Only reconcile when every employer contribution has been safely extracted.
                var govt=entries.Where(x=>x.Kind==EntryKind.Informational && Has(x.Label,"حكومة")).ToList();
                if(govt.Count<4)warnings.Add("Employer contributions incomplete; cannot verify net equation");
                else if(Math.Abs(gross.Value + govt.Sum(x=>x.Amount) - deduct.Value - net.Value)>.02m)
                    warnings.Add("Net does not reconcile with gross + government shares - deductions");
            }
            if(!entries.Any(x=>x.Kind==EntryKind.Earning)) warnings.Add("No detailed earnings could be safely linked");
            if(!entries.Any(x=>x.Kind==EntryKind.Deduction)) warnings.Add("No detailed deductions could be safely linked");
            foreach(var w in warnings)issues.Add(new("PAYSLIP_REVIEW",w,r.Start));
            employees.Add(new(id.Length==14?id:null,pay.Length>0?pay:null,name,bank,gross,deduct,net,entries,warnings,r.Start,r.Pages[^1].Number));
        }
        var dupe=employees.Where(e=>e.NationalId is not null).GroupBy(e=>e.NationalId).Where(g=>g.Count()>1);
        foreach(var g in dupe)issues.Add(new("DUPLICATE_EMPLOYEE",$"Duplicate national ID ({g.Count()} records)",null,"error"));
        return Report(file,ReportKind.EmployeePayslip,pages.Count,employees,[],issues);
    }
    private static List<PayrollEntry> ExtractEntries(List<(int Number,string Text)> pages,List<string> warnings) {
        var entries=new List<PayrollEntry>();
        var state=EntryKind.Informational;string? code=null;var buf=new List<string>();int itemPage=pages[0].Number;
        static bool IgnoreLine(string l)=>l.Length==0 || (Has(l,"الاسم") && Has(l,"القيمة")) ||
            Has(l,"نظام أوراكل") || Has(l,"المرتب عن شهر") || Has(l,"هذا البيان للمراجعة") || Has(l,"كشف رواتب");
        void Flush() {
            if(buf.Count==0)return;
            var raw=string.Join(" ",buf);buf.Clear();
            if(raw.TrimStart().StartsWith("x ",Ignore)||raw.TrimStart().StartsWith("× ",Ignore))return;
            var amounts=Money.Matches(raw).Select(m=>(Text:m.Value,Index:m.Index,Length:m.Length))
                .Where(x=>!Regex.IsMatch(x.Text,@"^(?:19|20)\d{2}$"))
                .Where(x=>!Regex.IsMatch(raw.Substring(Math.Min(raw.Length,x.Index+x.Length)).TrimStart(),@"^%"))
                .ToArray();
            if(amounts.Length==0)return;
            // Choose final currency-like token, allowing values embedded before trailing Arabic text.
            var amountToken=amounts[^1];
            if(Arabic.Amount(amountToken.Text) is not {} amount)return;
            var label=raw.Remove(amountToken.Index,amountToken.Length).Trim();
            if(label.Length<3)return;
            if(Has(label,"اجمالي") || Has(label,"الأجمالى") || Has(label,"الصافي"))return;
            if(code is null && !(state==EntryKind.Informational && Has(label,"حكومة"))) {
                warnings.Add("Unanchored item: "+raw[..Math.Min(raw.Length,70)]);
                return;
            }
            entries.Add(new PayrollEntry(code,label,amount,state,itemPage,raw));
        }
        foreach(var page in pages) {
            foreach(var original in Arabic.Lines(page.Text)) {
                var line=original.Trim();
                if(GrossLine(line)) {Flush();state=EntryKind.Informational;code=null;continue;}
                if(DeductionLine(line)) {Flush();state=EntryKind.Informational;code=null;continue;}
                if(NetLine(line)) {Flush();state=EntryKind.Informational;code=null;continue;}
                if(Has(line,"الاستحقاقات") && !Has(line,"اجمالى") && !Has(line,"اجمالي")){Flush();state=EntryKind.Earning;code=null;continue;}
                if(Has(line,"الاستقطاعات") && !Has(line,"اجمالى") && !Has(line,"اجمالي")){Flush();state=EntryKind.Deduction;code=null;continue;}
                if(IgnoreLine(line))continue;
                if(line.TrimStart().StartsWith("x ",Ignore)){Flush();code=null;continue;}
                var match=Account.Match(line);
                if(match.Success) {
                    var prefix=line[..match.Index].Trim();
                    var suffix=line[(match.Index+match.Length)..].Trim();
                    bool atEnd=suffix.Length==0 && prefix.Length>0;
                    if(atEnd) {
                        // Deduction rows commonly end with their account code.
                        if(buf.Count>0)Flush();
                        code=match.Value;itemPage=page.Number;
                        buf.Add(prefix);Flush();code=null;
                    }else {
                        Flush();code=match.Value;itemPage=page.Number;
                        if(suffix.Length>0)buf.Add(suffix);
                    }
                    continue;
                }
                if(state is EntryKind.Earning or EntryKind.Deduction) {
                    if(buf.Count>0)buf.Add(line);
                    else if(Has(line,"حكومة")) {code=null;itemPage=page.Number;buf.Add(line);}
                } else if(Has(line,"حكومة")) {Flush();code=null;itemPage=page.Number;buf.Add(line);}
                else if(buf.Count>0)buf.Add(line);
            }
        }
        Flush();return entries;
    }
    private ImportReport Cover(string file,IReadOnlyList<string> pages) {
        var groups=new List<CoverGroup>();var issues=new List<ImportIssue>();
        string? bank=null,branch=null;int start=1;
        for(int i=0;i<pages.Count;i++) {
            var t=pages[i];var b=Labeled(t,"البنك","الفرع") ?? Labeled(t,"البنك ","الفرع");
            if(b is not null){bank=b;start=i+1;}
            var f=Field(t,"الفرع");if(f is not null) branch=f;
            var m=Regex.Match(t,@"جملة\s+الصاف[ىي]\s+المستحق\s+([\d,.]+)");
            if(!m.Success)continue;
            var net=Arabic.Amount(m.Groups[1].Value);
            var n=Regex.Match(t,@"عدد\s+العاملين\s*:\s*(\d+)");
            int? count=n.Success && int.TryParse(n.Groups[1].Value,out var x)?x:null;
            var warns=new List<string>();
            if(net is null||count is null)warns.Add("Unverified bank group total or employee count");
            groups.Add(new(bank,branch,count,net,start,warns));
            foreach(var w in warns)issues.Add(new("COVER_REVIEW",w,i+1));
        }
        if(groups.Count==0)issues.Add(new("NO_COVER_GROUPS","No recognized group totals",null,"error"));
        return Report(file,ReportKind.Cover,pages.Count,[],groups,issues);
    }
    private ImportReport Payment(string file,IReadOnlyList<string> pages) {
        var employees=new List<PayrollEmployee>();var groups=new List<CoverGroup>();var issues=new List<ImportIssue>();
        string? bank=null,branch=null;decimal runningNet=0;int runningCount=0,startPage=1;bool hasGroup=false;
        for(int pi=0;pi<pages.Count;pi++) {
            var t=pages[pi];var lines=Arabic.Lines(t);
            var bankMatch=Labeled(t,"البنك","الفرع") ?? Labeled(t,"البنك ","الفرع");
            if(bankMatch is not null) {bank=bankMatch;branch=Field(t,"الفرع");}
            var end=Array.FindIndex(lines,l=>Has(l,"إقرار"));
            if(end<0)end=lines.Length;
            var section=Array.FindLastIndex(lines,Math.Max(0,end-1),end,l=>Has(l,"الاستقطاعات"));
            if(section<0)section=0;
            for(var i=section+1;i<end;i++) {
                var row=lines[i];var cash=Currency.Matches(row).Select(m=>m.Value).ToList();
                if(cash.Count!=11)continue;
                // Aggregate rows and continuation pages don't carry an employee marker.
                var heading=string.Join(" ",lines.Skip(Math.Max(section,i-3)).Take(Math.Min(3,i-section)));
                if(Has(heading,"اجمالي السجل")||Has(heading,"إجمالى السجل")) continue;
                int second=-1;
                for(var j=i+1;j<=Math.Min(i+3,end-1);j++)
                    if(Currency.Matches(lines[j]).Count==11){second=j;break;}
                if(second<0)continue;
                var b=Currency.Matches(lines[second]).Select(m=>m.Value).ToArray();
                var gross=Arabic.Amount(cash[9]);var net=Arabic.Amount(cash[10]);var deduction=Arabic.Amount(b[0]);
                if(gross is null || net is null || deduction is null || Math.Abs(gross.Value-net.Value-deduction.Value)>.02m)continue;
                // Only treat a numeric pair as an employee if the nearby text identifies a person.
                var firstMoney=Currency.Match(row);
                var prefix=row[..firstMoney.Index].Trim();
                var suffix=row[(Currency.Matches(row)[^1].Index+Currency.Matches(row)[^1].Length)..].Trim();
                var prev=i>0?lines[i-1]:"";
                var name=prefix.Length>2 && Regex.IsMatch(prefix,@"[\u0621-\u064a]")?prefix:
                    Regex.IsMatch(prev,@"[\u0621-\u064a]")?prev:"";
                if(name.Length<5)continue;
                // Continuation word may appear after the final numeric value.
                var tail=Regex.Replace(suffix,@"\d{4,6}-\d{5,6}|\b\d{2,3}\b","").Trim();
                if(tail.Length>1 && Regex.IsMatch(tail,@"[\u0621-\u064a]")) name=(name+" "+tail).Trim();
                var near=string.Join(" ",lines.Skip(Math.Max(section,i-3)).Take(Math.Min(end,second+1)-Math.Max(section,i-3)));
                var reverse=ReversePay.Match(near);
                // Reverse code is unambiguous only when its missing suffix is actually present.
                string? payId=null;
                if(reverse.Success){
                    var candidate=reverse.Groups[2].Value;string? suffixDigits=null;
                    if(candidate.Length==6) suffixDigits=Regex.Match(suffix,@"\b\d{2}\b").Value;
                    else suffixDigits=Regex.Match(suffix,@"\b\d{3}\b").Value;
                    if(suffixDigits.Length>0)payId=candidate+suffixDigits+"-"+reverse.Groups[1].Value;
                }
                string? nid=null;
                // The 14-digit Egyptian ID is split into a 12-digit line and 2-digit prefix.
                if(second>i+1){
                    var part=Regex.Match(lines[second-1],@"(?<!\d)\d{12}(?!\d)");
                    var part2=Regex.Match(lines[second],@"^\s*(\d{2})\s");
                    if(part.Success&&part2.Success)nid=part.Value+part2.Groups[1].Value;
                }
                var warn=new List<string>();
                if(nid is null)warn.Add("Payment row has no proven national ID");
                if(payId is null)warn.Add("Payment row has no proven payment number");
                if(gross.Value-net.Value!=deduction.Value) { /* handled within tolerance above */ }
                employees.Add(new(nid,payId,name,bank,gross,deduction,net,[],warn,pi+1,pi+1));
                foreach(var w in warn)issues.Add(new("PAYMENT_REVIEW",w,pi+1));
                runningNet+=net.Value;runningCount++;hasGroup=true;i=second;
            }
            // A record group may continue onto a subsequent page. Trust only its printed subtotal.
            foreach(Match m in Regex.Matches(t,@"(?m)^\s*إجمالي?\s+السجل\s*\n\s*([\d,.]+)")){
                var total=Arabic.Amount(m.Groups[1].Value);
                var warns=new List<string>();
                if(total is null) warns.Add("Group net could not be extracted");
                else if(!hasGroup || Math.Abs(runningNet-total.Value)>.02m)
                    warns.Add("Employee net sum does not match group reported net");
                var g=new CoverGroup(bank,branch,runningCount,total,startPage,warns);
                groups.Add(g);foreach(var w in warns)issues.Add(new("PAYMENT_GROUP_REVIEW",w,pi+1));
                startPage=pi+2;runningNet=0;runningCount=0;hasGroup=false;
            }
        }
        if(hasGroup)issues.Add(new("INCOMPLETE_PAYMENT_GROUP","Group has employee records but no closing subtotal",pages.Count));
        if(employees.Count==0)issues.Add(new("NO_PAYMENT_ROWS","No verified employee rows extracted",null,"error"));
        if(groups.Count==0)issues.Add(new("NO_PAYMENT_GROUPS","No group subtotal identified",null,"error"));
        foreach(var dup in employees.Where(e=>e.NationalId is not null).GroupBy(e=>e.NationalId).Where(g=>g.Count()>1))
            issues.Add(new("DUPLICATE_EMPLOYEE","Repeated national ID in payment sheet",null,"error"));
        return Report(file,ReportKind.PaymentSheet,pages.Count,employees,groups,issues);
    }
    private static ImportReport Report(string file,ReportKind kind,int pages,List<PayrollEmployee> employees,List<CoverGroup> groups,List<ImportIssue> issues){
        // Only mark verified if every expected piece is present and no ambiguity remains.
        var complete=issues.Count==0&&((kind==ReportKind.EmployeePayslip&&employees.Count>0&&employees.All(e=>e.Gross is not null&&e.Deductions is not null&&e.Net is not null))
            ||(kind==ReportKind.Cover&&groups.Count>0&&groups.All(g=>g.EmployeeCount is not null&&g.TotalNet is not null))
            ||(kind==ReportKind.PaymentSheet&&groups.Count>0&&employees.Count>0&&employees.All(e=>e.NationalId is not null&&e.DisbursementNumber is not null)));
        return new(Guid.NewGuid(),file,kind,complete?VerificationStatus.Verified:VerificationStatus.ReviewRequired,pages,employees,groups,issues);
    }
}