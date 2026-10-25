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
    private static readonly Regex Account = new(@"(?<!\d)\d{8}(?!\d)",RegexOptions.Compiled);
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
        var report=kind switch {
            ReportKind.EmployeePayslip => Payslips(fileName,clean),
            ReportKind.Cover => Cover(fileName,clean),
            ReportKind.PaymentSheet => Payment(fileName,clean),
            _=>new(Guid.NewGuid(),fileName,kind,VerificationStatus.Rejected,pages.Count,[],[],[new("UNKNOWN_REPORT","Unknown report structure",null,"error")])
        };
        return report with {Period=FindPeriod(sample)};
    }
    private static string? FindPeriod(string text){
      var months=new[]{"يناير","فبراير","مارس","ابريل","مايو","يونيو","يوليو","اغسطس","سبتمبر","اكتوبر","نوفمبر","ديسمبر"};
      var m=Regex.Match(text,@"(?:عن\s+)?شهر\s*:\s*([\u0621-\u064a]+).*?سنه\s*:?\s*(\d{4})",RegexOptions.Singleline);
      if(!m.Success)return null;
      var index=Array.FindIndex(months,x=>x==K(m.Groups[1].Value));
      return index<0?null:m.Groups[2].Value+"-"+(index+1).ToString("00",CultureInfo.InvariantCulture);
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
            if(gross is not null){
                var earningsSum=entries.Where(x=>x.Kind==EntryKind.Earning).Sum(x=>x.Amount);
                if(Math.Abs(earningsSum-gross.Value)>.02m)warnings.Add("Earning line items do not reconcile with printed gross");
            }
            if(deduct is not null){
                var deductionSum=entries.Where(x=>x.Kind==EntryKind.Deduction).Sum(x=>x.Amount);
                if(Math.Abs(deductionSum-deduct.Value)>.02m)warnings.Add("Deduction line items do not reconcile with printed deduction total");
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
            Has(l,"نظام أوراكل") || Has(l,"المرتب عن شهر") || Has(l,"هذا البيان للمراجعة") || Has(l,"كشف رواتب") || l.Contains("BR_",StringComparison.OrdinalIgnoreCase);
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
            var amountToken=amounts.LastOrDefault(x=>x.Text.Contains('.') || x.Text.Contains(','));
            if(amountToken==default)amountToken=amounts[^1];
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
                if(line.TrimStart().StartsWith("x",Ignore)){Flush();code=null;continue;}
                var match=Account.Match(line);
                if(match.Success) {
                    // Continuation earnings can follow the summary on the next page.
                    if(state==EntryKind.Informational && match.Value.StartsWith("2111",StringComparison.Ordinal)) state=EntryKind.Earning;
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
                    if(code is not null || buf.Count>0)buf.Add(line);
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
        // A new cover group starts at its standalone entitlement heading.
        // This is a semantic boundary, not a page number or a fixed column position.
        var declaredGroups=pages.Count(page=>Arabic.Lines(page).Any(line=>K(line)==K("الاستحقاقات")));
        if(declaredGroups!=groups.Count)
            issues.Add(new("INCOMPLETE_COVER_GROUPS",
                $"Found {declaredGroups} group openings but {groups.Count} closing totals",null,"error"));
        if(groups.Count==0)issues.Add(new("NO_COVER_GROUPS","No recognized group totals",null,"error"));
        return Report(file,ReportKind.Cover,pages.Count,[],groups,issues);
    }
    private ImportReport Payment(string file,IReadOnlyList<string> pages) {
        var employees=new List<PayrollEmployee>();var groups=new List<CoverGroup>();var issues=new List<ImportIssue>();
        string? bank=null,branch=null;decimal runningNet=0;int runningCount=0,startPage=1;
        (string First,string Previous,string Region,int Page,string? Bank)? pending=null;
        bool ParsePair(string first,string second,string previous,string identity,string region,int sourcePage,string? sourceBank) {
            var a=Currency.Matches(first).Select(m=>m.Value).ToArray();
            var b=Currency.Matches(second).Select(m=>m.Value).ToArray();
            if(a.Length!=11||b.Length!=11)return false;
            var ded=Arabic.Amount(b[0]);if(ded is null)return false;
            var normalGross=Arabic.Amount(a[9]);var normalNet=Arabic.Amount(a[10]);
            var invertedGross=Arabic.Amount(a[1]);var invertedNet=Arabic.Amount(a[0]);
            decimal? gross=null,net=null;bool reordered=false;
            if(normalGross is not null&&normalNet is not null&&Math.Abs(normalGross.Value-normalNet.Value-ded.Value)<=.02m) {
                gross=normalGross;net=normalNet;
            }else if(invertedGross is not null&&invertedNet is not null&&Math.Abs(invertedGross.Value-invertedNet.Value-ded.Value)<=.02m){
                gross=invertedGross;net=invertedNet;reordered=true;
            }else return false;
            var ms=Currency.Matches(first);var prefix=first[..ms[0].Index].Trim();
            var suffix=first[(ms[^1].Index+ms[^1].Length)..].Trim();
            var context=region.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            var name=Regex.IsMatch(prefix,@"[\u0621-\u064a]")?prefix:previous;
            if(!Regex.IsMatch(prefix,@"[\u0621-\u064a]") && context.Length>=3){
                var prior=context[^3];
                if(Regex.IsMatch(previous,@"[\u0621-\u064a]") && previous.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length==1
                    && Regex.IsMatch(prior,@"[\u0621-\u064a]") && prior.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=2)
                    name=prior+" "+previous;
            }
            // Split surname may follow the last numeric column, before an account identifier.
            var tail=Regex.Replace(suffix,@"\d{4,6}-\d{5,6}|\b\d{2,3}\b","").Trim();
            if(tail.Length>1 && Regex.IsMatch(tail,@"[\u0621-\u064a]"))name=(name+" "+tail).Trim();
            name=Regex.Replace(name,@"\s+"," ").Trim();
            if(name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<2
                || !Regex.IsMatch(name,@"[\u0621-\u064a]")
                || Has(name,"اجمالى السجل") || Has(name,"إجمالي السجل")
                || Has(name,"الاستقطاعات"))return false;
            var n12=Regex.Match(identity,@"(?<!\d)\d{12}(?!\d)");
            var suffixId=Regex.Match(second,@"^\s*(\d{2})\s");
            var national=n12.Success&&suffixId.Success?n12.Value+suffixId.Groups[1].Value:null;
            // Prefer an identifier embedded in the same row; otherwise the closest
            // preceding 4–6 / 5–6 digit Oracle identifier, delimited by this employee's row.
            var reversed=ReversePay.Match(suffix);
            var linesBefore=context.ToArray();
            if(!reversed.Success){
                var candidates=ReversePay.Matches(region).Cast<Match>().ToArray();
                if(candidates.Length>0)reversed=candidates[^1];
            }
            string? payment=null;
            if(reversed.Success) {
                var head=reversed.Groups[2].Value;
                var width=head.Length==6?2:head.Length==5?3:0;
                if(width>0){
                    var ending=Regex.Matches(suffix,@"(?<!\d)\d{2,3}(?!\d)")
                        .Select(m=>m.Value).FirstOrDefault(value=>value.Length==width);
                    if(ending is null){
                        var codeLine=Array.FindLastIndex(linesBefore,
                            x=>ReversePay.IsMatch(x));
                        if(codeLine>=0){
                            // Oracle may print the payment prefix digits on their own text line.
                            ending=linesBefore.Skip(codeLine+1).Take(Math.Max(0,linesBefore.Length-codeLine-2))
                                .Select(x=>x.Trim())
                                .FirstOrDefault(x=>x.Length==width && x.All(char.IsDigit));
                        }
                    }
                    if(ending is not null)payment=head+ending+"-"+reversed.Groups[1].Value;
                }
            }
            var warnings=new List<string>();
            if(national is null)warnings.Add("National identifier not proven for employee row");
            if(payment is null)warnings.Add("Disbursement identifier not proven for employee row");
            // A unique matching gross-minus-deductions equation validates the alternate RTL column stream.
            employees.Add(new(national,payment,name,sourceBank,gross,ded,net,[],warnings,sourcePage,sourcePage));
            foreach(var w in warnings)issues.Add(new("PAYMENT_ROW_REVIEW",w,sourcePage));
            runningNet+=net.Value;runningCount++;
            return true;
        }
        for(int pi=0;pi<pages.Count;pi++) {
            var t=pages[pi];var lines=Arabic.Lines(t);
            var currentBank=Labeled(t,"البنك","الفرع")??Labeled(t,"البنك ","الفرع");
            if(currentBank is not null) {bank=currentBank;branch=Field(t,"الفرع");}
            var end=Array.FindIndex(lines,l=>Has(l,"إقرار"));if(end<0)end=lines.Length;
            var section=Array.FindLastIndex(lines,Math.Max(0,end-1),end,l=>Has(l,"الاستقطاعات"));
            if(section<0)section=0;
            if(pending is not null) {
                // A complete two-row employee record may be split across consecutive PDF pages.
                bool completed=false;
                for(int k=section+1;k<Math.Min(end-1,section+14);k++) {
                    if(!Regex.IsMatch(lines[k],@"(?<!\d)\d{12}(?!\d)"))continue;
                    if(Currency.Matches(lines[k+1]).Count!=11)continue;
                    var x=pending.Value;
                    if(ParsePair(x.First,lines[k+1],x.Previous,lines[k],x.Region,x.Page,x.Bank)) completed=true;
                    break;
                }
                if(!completed)issues.Add(new("UNRESOLVED_PAGE_SPLIT","Employee row split across pages could not be reconciled",pending.Value.Page,"error"));
                pending=null;
            }
            for(int index=section+1;index<end;index++){
                var first=lines[index];if(Currency.Matches(first).Count!=11)continue;
                // Employee names may be split onto lines before the monetary columns;
                // never classify a group-total heading as an employee name.
                var initialPrefix=first[..Currency.Match(first).Index].Trim();
                var previous=index>section+1?lines[index-1]:"";
                var before=index>section+2?lines[index-2]:"";
                var nameHint=initialPrefix.Length>0?initialPrefix:previous;
                if((Has(nameHint,"اجمالى") || Has(nameHint,"إجمالي") || Has(nameHint,"الاستقطاعات"))
                    || !Regex.IsMatch(nameHint,@"[\u0621-\u064a]"))continue;
                var begin=Math.Max(section+1,index-6);
                var region=string.Join("\n",lines.Skip(begin).Take(index-begin+1));
                int secondIndex=-1;
                for(var k=index+1;k<Math.Min(index+4,end);k++)
                    if(Currency.Matches(lines[k]).Count==11){secondIndex=k;break;}
                if(secondIndex>=0) {
                    string identity=secondIndex>index+1?lines[secondIndex-1]:"";
                    if(ParsePair(first,lines[secondIndex],previous,identity,region,pi+1,bank))index=secondIndex;
                }else if(pi+1<pages.Count) {
                    pending=(first,previous,region,pi+1,bank);
                }
            }
            // Printed bank-group subtotal is the evidence source; never invent it from rows.
            var close=Array.FindIndex(lines,l=>Has(l,"اجمالى السجل")||Has(l,"إجمالي السجل"));
            if(close>=0){
                decimal? official=null;
                if(close+1<lines.Length) {
                   var sumLine=lines[close+1].Trim();
                   var token=Regex.Match(sumLine,@"^\s*(\d+(?:,\d{3})*\.\d{2})\b");
                   if(token.Success)official=Arabic.Amount(token.Groups[1].Value);
                }
                // Some Oracle reports split the final decimal digit onto the next text line.
                // Recover only a unique printed candidate equal to the independently summed rows.
                if(official is null) {
                    var reconstructed=new List<decimal>();
                    for(var j=close+1;j<Math.Min(lines.Length-1,close+7);j++){
                        var part=Regex.Match(lines[j],@"(?<!\d)(\d{4,}\.\d)\s*$");
                        if(!part.Success || !Regex.IsMatch(lines[j+1],@"^\d$"))continue;
                        if(Arabic.Amount(part.Groups[1].Value+lines[j+1]) is {} candidate)
                            reconstructed.Add(candidate);
                    }
                    if(reconstructed.Count(x=>Math.Abs(x-runningNet)<=.02m)==1)
                        official=reconstructed.Single(x=>Math.Abs(x-runningNet)<=.02m);
                }
                var warnings=new List<string>();
                if(official is null)warnings.Add("Official group subtotal is unreadable or fragmented");
                else if(Math.Abs(official.Value-runningNet)>.02m) warnings.Add("Employee net sum differs from printed group subtotal");
                var declared=Regex.Match(t,@"البالغ\s+عددهم\s+(\d+)");
                if(declared.Success && int.TryParse(declared.Groups[1].Value,out var officialCount) && officialCount!=runningCount)
                    warnings.Add("Extracted employee count differs from declared bank group count");
                if(runningCount==0)warnings.Add("Group has no extracted employee rows");
                groups.Add(new(bank,branch,runningCount,official,startPage,warnings));
                foreach(var w in warnings)issues.Add(new("PAYMENT_GROUP_REVIEW",w,pi+1));
                runningCount=0;runningNet=0;startPage=pi+2;
            }
        }
        if(pending is not null) issues.Add(new("TRUNCATED_ROW","PDF ends in the middle of an employee",pending.Value.Page,"error"));
        if(runningCount>0)issues.Add(new("INCOMPLETE_GROUP","Employee data without closing bank subtotal",pages.Count,"error"));
        if(employees.Count==0)issues.Add(new("NO_PAYMENT_EMPLOYEES","No reliable payment rows",null,"error"));
        if(groups.Count==0)issues.Add(new("NO_PAYMENT_GROUPS","No bank group totals",null,"error"));
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