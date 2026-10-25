using System.Text.RegularExpressions;
using OraclePdfParser.Domain;

namespace OraclePdfParser.Application;

public sealed class PayslipParser
{
    private static readonly Regex Identity = new(@"اسم الموظف\s*:\s*(?<name>.*?)\s+رقم الصرف\s*:\s*(?<payment>[\d-]+)\s+الرقم القوم[ىي]\s*:\s*(?<id>\d{14})", RegexOptions.Compiled);
    private static readonly Regex CodeStart = new(@"^(?<code>[24]\d{7})(?:\s+(?<rest>.*))?$", RegexOptions.Compiled);
    private static readonly Regex CodeEnd = new(@"^(?<rest>.+?)\s+(?<code>[24]\d{7})$", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"(?<!\d)\d[\d,.]*(?!\d)", RegexOptions.Compiled);

    public IReadOnlyList<EmployeePayroll> Parse(PdfDocumentText doc)
    {
        var result = new List<EmployeePayroll>();
        EmployeePayroll? current = null;
        var lines = new List<(int Page,string Text)>();
        foreach (var page in doc.Pages)
        {
            var text = ArabicText.Normalize(page.Text);
            var match = Identity.Match(Regex.Replace(text, @"\s+", " "));
            if (match.Success)
            {
                if (current != null) { ReadDetails(current, lines); result.Add(current); lines.Clear(); }
                current = new EmployeePayroll {
                    Name = match.Groups["name"].Value.Trim(),
                    PaymentNumber = match.Groups["payment"].Value,
                    NationalId = match.Groups["id"].Value, FirstPage = page.Number,
                    LastPage = page.Number
                };
                var bank = Regex.Match(text, @"البنك\s*:\s*(?<bank>.*?)\s+الفرع\s*:\s*(?<branch>[^\n]+)");
                if (bank.Success) { current.Bank = bank.Groups["bank"].Value.Trim(); current.Branch = bank.Groups["branch"].Value.Trim(); }
            }
            if (current != null)
            {
                current.LastPage = page.Number;
                lines.AddRange(ArabicText.Lines(text).Select(s => (page.Number, s)));
            }
        }
        if (current != null) { ReadDetails(current,lines); result.Add(current); }
        return result;
    }

    private static bool IsTotal(string line, string phrase) =>
        ArabicText.Key(line).Contains(ArabicText.Key(phrase), StringComparison.Ordinal);
    private static bool IsAccount(string line) =>
        CodeStart.IsMatch(line) || CodeEnd.IsMatch(line);
    private static void ReadDetails(EmployeePayroll employee, List<(int Page,string Text)> lines)
    {
        ItemKind section = ItemKind.Earning;
        string? pending = null;
        int pendingPage=0;
        bool pastEarningsTotal = false;
        void Flush()
        {
            if (pending == null) return;
            var raw = pending;
            var originalSource = pending;
            pending = null;
            string? code = null;
            var start = CodeStart.Match(raw);
            if (start.Success) { code = start.Groups["code"].Value; raw = start.Groups["rest"].Value; }
            else
            {
                var end = CodeEnd.Match(raw);
                if (end.Success) { code = end.Groups["code"].Value; raw = end.Groups["rest"].Value; }
            }
            // Some Oracle reports put an account code after a wrapped row.
            var trailing = Regex.Match(raw, @"\s+(?<code>[24]\d{7})$");
            if (trailing.Success) { code ??= trailing.Groups["code"].Value; raw = raw[..trailing.Index]; }
            bool subtotal = Regex.IsMatch(raw, @"^x(?:\s|$)", RegexOptions.IgnoreCase);
            var nums = Money.Matches(raw).Cast<Match>()
                .Where(m => m.Index + m.Length >= raw.Length || raw[m.Index + m.Length] != '%')
                .ToList();
            // Distinguish 2014/2015, legislative labels, and percentages from an actual money amount.
            var candidates = nums.Where(m => m.Value.Contains('.') || m.Value.Contains(',')).ToList();
            Match? selected = candidates.Count > 0 ? candidates[^1] : nums.Count > 0 ? nums[^1] : null;
            if (selected is null || !ArabicText.TryMoney(selected.Value, out decimal amount))
            {
                employee.Issues.Add(new(IssueSeverity.Warning,"UNPARSED_ITEM",
                    "تعذر تحديد المبلغ: " + originalSource, pendingPage, employee.NationalId));
                return;
            }
            string label = (raw[..selected.Index] + " " + raw[(selected.Index+selected.Length)..]).Trim();
            label = Regex.Replace(label, @"\s+", " ");
            if (label.Length == 0) { employee.Issues.Add(new(IssueSeverity.Warning,"EMPTY_ITEM_NAME",raw,pendingPage,employee.NationalId)); return; }
            var kind = subtotal ? ItemKind.Subtotal : code?.StartsWith("2112",StringComparison.Ordinal)==true ?
                ItemKind.EmployerContribution : section;
            employee.Items.Add(new PayrollItem(code,label,amount,kind,pendingPage,originalSource));
        }

        foreach (var (page,line) in lines)
        {
            if (IsTotal(line,"أجمالى الاستحقاقات") || IsTotal(line,"إجمالى الاستحقاقات"))
            {
                Flush(); employee.Earnings = ArabicText.LastMoney(line);
                pastEarningsTotal = true; continue;
            }
            if (IsTotal(line,"الاجمالى الاستقطاعات") || IsTotal(line,"الأجمالى الاستقطاعات"))
            {
                Flush(); employee.Deductions = ArabicText.LastMoney(line); continue;
            }
            if (Regex.IsMatch(ArabicText.Key(line), @"^الصافي\s*:"))
            { Flush(); employee.Net = ArabicText.LastMoney(line); continue; }
            if (ArabicText.Key(line) == ArabicText.Key("الاستقطاعات"))
            { Flush(); section=ItemKind.Deduction; continue; }
            if (ArabicText.Key(line) == ArabicText.Key("الاستحقاقات"))
            { Flush(); section=ItemKind.Earning; continue; }
            if (line.StartsWith("اسم الموظف",StringComparison.Ordinal)
                || line.StartsWith("نظام أوراكل",StringComparison.Ordinal)
                || line.StartsWith("المرتب عن شهر",StringComparison.Ordinal)
                || line.StartsWith("كشف رواتب",StringComparison.Ordinal)
                || line.StartsWith("BR_",StringComparison.Ordinal)
                || line.StartsWith("نوع الصرف",StringComparison.Ordinal)
                || line.StartsWith("الرقم التامينى",StringComparison.Ordinal)
                || line.StartsWith("هذا البيان",StringComparison.Ordinal)
                || line.StartsWith("الاسم القيمة",StringComparison.Ordinal)) continue;
            bool startsCode = CodeStart.IsMatch(line);
            bool endsCode = CodeEnd.IsMatch(line);
            bool subtotal = Regex.IsMatch(line, @"^x\s", RegexOptions.IgnoreCase);
            if (startsCode || subtotal)
            {
                Flush(); pending=line; pendingPage=page;
            }
            else if (endsCode && pending is null)
            {
                pending=line; pendingPage=page; Flush();
            }
            else if (endsCode && pending is not null)
            {
                Flush(); pending=line; pendingPage=page; Flush();
            }
            else if (pending is not null) pending += " " + line;
            // Only take lines in item sections, never infer values from a header.
        }
        Flush();
        if (!pastEarningsTotal) employee.Issues.Add(new(IssueSeverity.Error,"MISSING_GROSS","إجمالي الاستحقاقات غير موجود",employee.FirstPage,employee.NationalId));
        if (employee.Net is null) employee.Issues.Add(new(IssueSeverity.Error,"MISSING_NET","صافي المرتب غير موجود",employee.LastPage,employee.NationalId));
        if (employee.Deductions is null) employee.Issues.Add(new(IssueSeverity.Error,"MISSING_DEDUCTIONS","إجمالي الاستقطاعات غير موجود",employee.LastPage,employee.NationalId));
        employee.EmployerContributions = employee.Items.Where(i=>i.Kind==ItemKind.EmployerContribution).Sum(i=>i.Amount);
        if (employee.Earnings is decimal gross && employee.Deductions is decimal deductions && employee.Net is decimal net)
        {
            var expected = gross + employee.EmployerContributions.Value - deductions;
            if (Math.Abs(expected-net) > 0.02m)
                employee.Issues.Add(new(IssueSeverity.Error,"PAYSLIP_RECONCILIATION",
                    $"صافي المرتب لا يتطابق: المتوقع {expected:0.00} والمطبوع {net:0.00}", employee.FirstPage,employee.NationalId));
        }
    }
}
