namespace OraclePdfParser.Domain;

public enum ReportKind { Unknown, EmployeePayslip, Cover, PaymentSheet }
public enum VerificationStatus { Verified, ReviewRequired, Rejected }
public enum EntryKind { Earning, Deduction, Informational, Subtotal }
public sealed record PayrollEntry(string? AccountCode, string Label, decimal Amount, EntryKind Kind, int Page, string RawText);
public sealed record PayrollEmployee(string? NationalId, string? DisbursementNumber, string Name, string? Bank, decimal? Gross, decimal? Deductions, decimal? Net, IReadOnlyList<PayrollEntry> Entries, IReadOnlyList<string> Warnings, int StartPage, int EndPage);
public sealed record CoverGroup(string? Bank, string? Branch, int? EmployeeCount, decimal? TotalNet, int Page, IReadOnlyList<string> Warnings);
public sealed record ImportIssue(string Code, string Message, int? Page, string Severity = "warning");
public sealed record ImportReport(Guid Id, string FileName, ReportKind Kind, VerificationStatus Status, int Pages, IReadOnlyList<PayrollEmployee> Employees, IReadOnlyList<CoverGroup> Groups, IReadOnlyList<ImportIssue> Issues) { public string? Period { get; init; } }
