# Architecture / قواعد الاستيراد

## Layers
- Domain: `PayrollImport`, `EmployeePayroll`, `PayrollItem`, `PaymentEntry`, `CoverEntry`, validation issues.
- Application: normalisation, payroll/stateful parsers, use-case coordination, cross-source reconciliation and ports.
- Infrastructure: PDFium C API via .NET P/Invoke; SQLite JSON snapshots stored locally.
- API: ASP.NET Core upload and query endpoints.
- UI: Angular standalone app, RTL, searchable payroll table, employee drilldown, issue list and CSV export.

## Invariants
1. Never use coordinates/column offsets/relative page pixel positions to determine item ownership.
2. Preserve the original text and page for every item.
3. Do not invent missing employee data from cover summaries.
4. Do not double-add subtotal (`x`) lines.
5. Resolve employment by national ID if unique; names alone are not reliable matching keys.
6. A value that fails monetary validation is flagged; review must precede use.
7. PDFs are sensitive: keep originals private. No cloud OCR services.
8. Never log national IDs, raw PDF text or PDFs.

## Limitations
PDF text order can be inconsistent in densely populated payment sheets; the current parser is conservative and does not yet recover every row variant. Full real-PDF acceptance (219/219 individual records and 56/56 cover groups with no blocking issues) remains a release gate, **not an assumed outcome**.
