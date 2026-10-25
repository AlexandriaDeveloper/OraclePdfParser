namespace OraclePdfParser.Domain;

public enum ReportType { Unknown, EmployeePayslip, PaymentSheet, Cover }
public enum ItemKind { Earning, Deduction, EmployerContribution, Subtotal }
public enum IssueSeverity { Information, Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, int? Page = null, string? EmployeeId = null);
public sealed record PayrollItem(string? AccountCode, string OriginalName, decimal Amount, ItemKind Kind, int SourcePage, string SourceText);
public sealed class EmployeePayroll
{
    public string NationalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PaymentNumber { get; set; }
    public string? Bank { get; set; }
    public string? Branch { get; set; }
    public decimal? Earnings { get; set; }
    public decimal? Deductions { get; set; }
    public decimal? Net { get; set; }
    public decimal? EmployerContributions { get; set; }
    public int FirstPage { get; set; }
    public int LastPage { get; set; }
    public List<PayrollItem> Items { get; set; } = new();
    public List<ValidationIssue> Issues { get; set; } = new();
}
public sealed record PaymentEntry(string NationalId, string Name, string? PaymentNumber, decimal Earnings, decimal Deductions, decimal Net, string Bank, string Branch, string Group, int Page);
public sealed record CoverEntry(string Bank, string Branch, string Group, int Workers, decimal Net, int Page);
public sealed record ImportedDocument(string FileName, string Sha256, ReportType Type, int Pages);
public sealed class PayrollImport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ImportedDocument> Documents { get; set; } = new();
    public List<EmployeePayroll> Employees { get; set; } = new();
    public List<PaymentEntry> Payments { get; set; } = new();
    public List<CoverEntry> Covers { get; set; } = new();
    public List<ValidationIssue> Issues { get; set; } = new();
    public decimal? NetFromPayslips => Employees.Count > 0 && Employees.All(e => e.Net.HasValue) ? Employees.Sum(e => e.Net!.Value) : null;
    public decimal? NetFromPayments => Payments.Count > 0 ? Payments.Sum(e => e.Net) : null;
    public decimal? NetFromCovers => Covers.Count > 0 ? Covers.Sum(e => e.Net) : null;
    public bool HasBlockingErrors => Issues.Any(e => e.Severity == IssueSeverity.Error) || Employees.Any(e => e.Issues.Any(i => i.Severity == IssueSeverity.Error));
}
public sealed record PdfPageText(int Number, string Text);
public sealed record PdfDocumentText(IReadOnlyList<PdfPageText> Pages);
