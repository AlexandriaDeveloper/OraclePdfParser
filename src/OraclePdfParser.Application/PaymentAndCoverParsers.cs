using System.Text.RegularExpressions;
using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;

public sealed class PaymentSheetParser
{
    // All financial values in this report have two decimal digits. Pair a row with the
    // deduction row through the financial identity, not through X/Y or a fixed line count.
    private static readonly Regex MoneyRun = new(@"(?<!\d)(?:\d[\d,]*\.\d+\s+){10}\d[\d,]*\.\d+(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"\d[\d,]*\.\d+", RegexOptions.Compiled);
    private static readonly Regex NationalIdFragments = new(@"(?m)(?<!\d)(?<start>\d{12})\s*(?<end>\d{2})\s*$",RegexOptions.Compiled);
    private static readonly Regex PaymentFragments = new(@"(?<!\d)(?<serial>\d{3,6})-(?<prefix>\d{5,6})(?!\d)",RegexOptions.Compiled);
    private sealed record CellRun(PdfPageText Page, string Text, int Start, int End, decimal[] Values,
        bool GroupTotal, string Bank, string Branch, string Group);

    public IReadOnlyList<PaymentEntry> Parse(PdfDocumentText doc, List<ValidationIssue> issues)
    {
        var runs = new List<CellRun>();
        foreach (var page in doc.Pages)
        {
            string text = ArabicText.Normalize(page.Text);
            string header = string.Join(" ",ArabicText.Lines(text).Take(13));
            string group = Header("مجموعة التعيين", "نوع صرف", header);
            string bank = Header("البنك", "الفرع", header);
            string branch = Regex.Match(text, @"الفرع\s*:\s*(?<value>[^\n]+)").Groups["value"].Value.Trim();

            foreach(Match match in MoneyRun.Matches(text))
            {
                var values = Amount.Matches(match.Value)
                    .Select(m=>ArabicText.TryMoney(m.Value,out var amount)?amount:(decimal?)null).ToArray();
                if(values.Length!=11 || values.Any(v=>v is null)) continue;
                bool groupTotal = text[..match.Index].Split('\n').TakeLast(3)
                    .Any(line=>ArabicText.Key(line).Contains("اجمال")&&ArabicText.Key(line).Contains("السج"));
                runs.Add(new CellRun(page,text,match.Index,match.Index+match.Length,
                    values.Select(v=>v!.Value).ToArray(),groupTotal,bank,branch,group));
            }
        }
        var payments=new List<PaymentEntry>();
        var used=new HashSet<int>();
        for (int i=0;i+1<runs.Count;i++)
        {
            var left=runs[i]; var right=runs[i+1];
            if(left.GroupTotal||right.GroupTotal || used.Contains(i) || used.Contains(i+1)) continue;
            decimal gross,net;
            if(Math.Abs(left.Values[9]-right.Values[0]-left.Values[10])<=.02m)
            {
                gross=left.Values[9];net=left.Values[10];
            }
            else if(Math.Abs(left.Values[1]-right.Values[0]-left.Values[0])<=.02m)
            {
                // Some Oracle content streams print columns in reverse on a single row.
                gross=left.Values[1];net=left.Values[0];
            }
            else continue;
            string between=left.Page.Number==right.Page.Number
                ?left.Text[left.End..right.Start]
                :left.Text[left.End..]+"\n"+right.Text[..right.Start];
            var idParts=NationalIdFragments.Match(between);
            if(!idParts.Success)
            {
                issues.Add(new(IssueSeverity.Error,"MISSING_PAYMENT_ID",
                    "تعذر استخراج الرقم القومي من صف الصرف",left.Page.Number));
                continue;
            }
            string nationalId=idParts.Groups["start"].Value+idParts.Groups["end"].Value;
            string name=ReadName(left.Text,left.Start,between);
            if(name.Length==0)
                issues.Add(new(IssueSeverity.Warning,"MISSING_PAYMENT_NAME","تعذر تحديد اسم الموظف من الصف",left.Page.Number,nationalId));
            string? paymentNumber=ReadPaymentNumber(left.Text,left.Start,between);
            if(paymentNumber==null)
                issues.Add(new(IssueSeverity.Warning,"PARTIAL_PAYMENT_NUMBER","رقم الصرف غير مكتمل في النص",left.Page.Number,nationalId));
            payments.Add(new PaymentEntry(nationalId,name,paymentNumber,gross,right.Values[0],
                net,left.Bank,left.Branch,left.Group,left.Page.Number));
            used.Add(i);used.Add(i+1);
        }
        // Any unmatched financial sequences may represent a damaged or unsupported layout.
        // Never silently treat a partial parse as a complete import.
        var nonTotals=Enumerable.Range(0,runs.Count).Where(i=>!runs[i].GroupTotal&&!used.Contains(i)).ToList();
        if(nonTotals.Count>0)
            issues.Add(new(IssueSeverity.Warning,"UNPAIRED_FINANCIAL_ROWS",
                $"{nonTotals.Count} سطر مالي لم يرتبط بحركة صرف موثقة",runs[nonTotals[0]].Page.Number));
        return payments;
    }

    private static string ReadName(string text,int start,string between)
    {
        var candidates=text[..start].Split('\n').Select(s=>s.Trim()).ToArray();
        var selected=new List<string>();
        var headerWords=new HashSet<string>(StringComparer.Ordinal)
        {
            "جمله","الاستقطاعات","استقطاعات","اخري","الاجمالي","القيمه","الاسم",
            "موحده","ضريبه","تامينات","شامل","اقساط"
        };
        for(int i=candidates.Length-1;i>=0 && selected.Count<3;i--)
        {
            string row=candidates[i];
            if(row.Length==0) continue;
            if(Regex.IsMatch(row,@"^[\p{L}\s_\-]+$") && row.Any(ch=>ch>='ء'&&ch<='ي')
                && !headerWords.Contains(ArabicText.Key(row)))
                selected.Insert(0,row);
            else break;
        }
        var name=string.Join(" ",selected);
        // Surnames sometimes occur AFTER the net amount. Keep Arabic text before the
        // identity fragments; exclude a new-page header when a payroll row spans pages.
        var beforeId=Regex.Match(between,@"(?s)^.*?(?=\d{12})").Value;
        int nextHeader=beforeId.IndexOf("وزارة",StringComparison.Ordinal);
        if(nextHeader>=0) beforeId=beforeId[..nextHeader];
        var surnames=Regex.Matches(beforeId,@"[ء-ي]+").Select(m=>m.Value).ToArray();
        if(surnames.Length>0) name+=" "+string.Join(" ",surnames);
        return Regex.Replace(name.Trim(),@"\s+"," ");
    }

    private static string? ReadPaymentNumber(string text,int start,string between)
    {
        string near=text[Math.Max(0,start-220)..start]+" "+Regex.Match(between,@"(?s)^.*?(?=\d{12})").Value;
        var parts=PaymentFragments.Matches(near);
        if(parts.Count==0) return null;
        var match=parts[^1];
        string prefix=match.Groups["prefix"].Value;
        string suffix=prefix.Length==5?"105":"05";
        return prefix+suffix+"-"+match.Groups["serial"].Value;
    }

    internal static string Header(string begin,string end,string text)
    {
        var pattern=Regex.Escape(begin)+@"\s*:\s*(.*?)"+(end.Length>0?@"\s+"+Regex.Escape(end)+@"\s*:":"$");
        var found=Regex.Match(text,pattern,RegexOptions.Singleline);
        return found.Success?found.Groups[1].Value.Trim():"";
    }
}
public sealed class CoverParser
{
    private static readonly Regex Net = new(@"جملة الصاف[ىي]\s+المستحق\s+(?<value>[\d,.]+)");
    private static readonly Regex Count = new(@"عدد العاملين\s*:\s*(?<count>\d+)");
    public IReadOnlyList<CoverEntry> Parse(PdfDocumentText doc,List<ValidationIssue> issues)
    {
        var covers = new List<CoverEntry>();
        var group = ""; var bank = ""; var branch = "";
        foreach (var page in doc.Pages)
        {
            string text = ArabicText.Normalize(page.Text);
            string header = string.Join(" ",ArabicText.Lines(text).Take(10));
            var nextGroup = PaymentSheetParser.Header("سجل رقم","نوع الصرف",header);
            var nextBank = PaymentSheetParser.Header("البنك","الفرع",header);
            var nextBranch = Regex.Match(text,@"الفرع\s*:\s*(?<value>[^\n]+)").Groups["value"].Value.Trim();
            if(nextGroup.Length>0) group=nextGroup;
            if(nextBank.Length>0) bank=nextBank;
            if(nextBranch.Length>0) branch=nextBranch;
            var net = Net.Match(text);
            if (!net.Success) continue;
            var count = Count.Match(text);
            if (!count.Success || !ArabicText.TryMoney(net.Groups["value"].Value,out var amount))
            { issues.Add(new(IssueSeverity.Error,"INVALID_COVER_TOTAL","تعذر قراءة صافي المجموعة أو عدد العاملين",page.Number)); continue; }
            covers.Add(new CoverEntry(bank,branch,group,int.Parse(count.Groups["count"].Value),amount,page.Number));
        }
        return covers;
    }
}
