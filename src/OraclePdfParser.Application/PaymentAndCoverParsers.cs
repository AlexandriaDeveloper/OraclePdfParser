using System.Text.RegularExpressions;
using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;

public sealed class PaymentSheetParser
{
    private static readonly Regex NameAndValues = new(@"^(?<name>[\p{L}\s_\-]+?)\s+(?<values>\d.*)$", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
    public IReadOnlyList<PaymentEntry> Parse(PdfDocumentText doc, List<ValidationIssue> issues)
    {
        var items = new List<PaymentEntry>();
        foreach (var page in doc.Pages)
        {
            var lines = ArabicText.Lines(page.Text).ToArray();
            string header = string.Join(" ",lines.Take(13));
            string group = Header("مجموعة التعيين", "نوع صرف", header);
            string bank = Header("البنك", "الفرع", header);
            string branch = Header("الفرع", "", header);
            for (int i=0;i<lines.Length-2;i++)
            {
                var payment = Regex.Match(lines[i], @"^(?<serial>\d{3,6})-(?<prefix>\d{6})$");
                if (!payment.Success) continue;
                var row = NameAndValues.Match(lines[i+1]);
                if (!row.Success) { issues.Add(new(IssueSeverity.Warning,"UNPARSED_PAYMENT","سطر اسم الموظف غير مفهوم",page.Number)); continue; }
                var amounts = Number.Matches(row.Groups["values"].Value).Select(m=>m.Value).ToArray();
                if (amounts.Length<3 || amounts[^1].Length!=2
                    || !ArabicText.TryMoney(amounts[^2],out var net)
                    || !ArabicText.TryMoney(amounts[^3],out var gross))
                { issues.Add(new(IssueSeverity.Warning,"UNPARSED_PAYMENT_AMOUNT","القيم الإجمالية غير واضحة",page.Number)); continue; }
                // Oracle prints the last two digits of the payroll register at the end of the first row.
                var fullPayment = payment.Groups["prefix"].Value + amounts[^1] + "-" + payment.Groups["serial"].Value;
                string id = "";
                decimal deduction = 0;
                if (i+3<lines.Length)
                {
                    var first = Regex.Match(lines[i+2], @"^\d{10,13}$");
                    var rest = Regex.Match(lines[i+3], @"^(?<tail>\d{1,4})\s+(?<ded>\d[\d,.]*)");
                    if(first.Success && rest.Success)
                    {
                        id = first.Value + rest.Groups["tail"].Value;
                        ArabicText.TryMoney(rest.Groups["ded"].Value,out deduction);
                    }
                }
                if (id.Length!=14)
                    issues.Add(new(IssueSeverity.Warning,"INVALID_PAYMENT_ID","الرقم القومي غير مكتمل",page.Number));
                if(Math.Abs(gross-deduction-net)>.02m)
                    issues.Add(new(IssueSeverity.Warning,"PAYMENT_ROW_MISMATCH","فرق حسابي في سطر كشف الصرف",page.Number,id));
                items.Add(new PaymentEntry(id,row.Groups["name"].Value.Trim(),fullPayment,gross,deduction,net,bank,branch,group,page.Number));
            }
        }
        return items;
    }
    internal static string Header(string begin,string end,string text)
    {
        var pattern = Regex.Escape(begin)+@"\s*:\s*(.*?)"+(end.Length>0?@"\s+"+Regex.Escape(end)+@"\s*:":"$");
        var found = Regex.Match(text,pattern,RegexOptions.Singleline);
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
            var nextBranch = PaymentSheetParser.Header("الفرع","",header);
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
