namespace OraclePdfParser.Application;

/// <summary>
/// Export one stored import report with an explicit employee scope and
/// a mix of official totals and selected individual payroll items.
/// Indexes are zero-based within the source report; no client-supplied amounts.
/// </summary>
public sealed record PayrollExcelRequest(
    Guid ReportId,
    bool AllEmployees,
    int[]? EmployeeIndexes,
    bool IncludeGross,
    bool IncludeDeductions,
    bool IncludeNet,
    string[]? ItemKeys);
