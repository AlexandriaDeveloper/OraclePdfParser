# OraclePdfParser

An Arabic RTL Oracle payroll PDF importer built with **ASP.NET Core 8**, **Angular 18** and separated **Domain / Application / Infrastructure / API** layers.

The parser uses PDFium's Unicode text stream, **not fixed page coordinates**, OCR or fixed employee/page counts. It treats unproven values as review items instead of fabricating links between names and amounts.

## Quick start

Prerequisites: .NET 8 SDK, Node.js 22 and npm.

```bash
dotnet run --project src/OraclePdfParser.Api --urls http://localhost:5080
```

In a separate terminal:

```bash
cd web
npm install
npm start
```

Open http://localhost:4200 and import one, two or three files. Accepted report kinds:

- **صرفية موظف** — one or more pages per employee, with detailed earnings and deductions.
- **كافر** — bank-group totals and employee counts, with pages that can continue.
- **كشوف صرف** — individual payment rows, deductions and bank-group totals, including split RTL fields.

The Angular UI supports search, page references, warnings, downloadable employee CSV, line-item CSV and original JSON review.

## Safety and correctness

- Uses native PDFium through the `PDFiumCore` runtime; does not rely on X/Y locations.
- Arabic presentation forms, Arabic/Indic digits, decimal separators, NFKC and variant spellings are normalized for matching.
- Monetary amounts use .NET `decimal`; data structures preserve original item text and source page.
- Payslip earnings / deductions and net including government contributions are checked.
- Payment gross minus deduction must equal net for each accepted payment row.
- Cover and payment groups are reconciled in order, with reported totals and number of employees.
- When multiple files are uploaded, match by national ID, validate net values and report period.
- Ambiguous rows, missing identifiers, split numbers, duplicate report types, failed totals and unrecognized columns remain **ReviewRequired**.
- The application **does not automatically approve payments**.
- Uploaded PDFs are processed in memory. Do not commit source payslips, national IDs or generated JSON/CSV to the public repository.

## Private real-file verification

Real source files are *not* committed to the repo, because they contain personal financial data. A standalone verifier is provided:

```bash
dotnet run --project tools/OraclePdfParser.Verify -- --strict \
  "path/to/صرفيه موظف(1).pdf" "path/to/كافر.pdf" "path/to/كشوف صرف.pdf"
```

For the provided **October 2026** reference set:

```bash
dotnet run --project tools/OraclePdfParser.Verify -- --golden-october-2026 \
  "path/to/صرفيه موظف(1).pdf" "path/to/كافر.pdf" "path/to/كشوف صرف.pdf"
```

Independently established PDFium reference controls: **219 employee payslips**, **56 cover groups**, **56 payment groups**, total payroll net **2,864,042.24 EGP**, and **219 employees in cover**. These are controls, not proof of each extracted item. Use `--strict` to additionally require zero parser review issues. The command returns a nonzero exit code for incorrect controls.

## Test suites

```bash
dotnet test tests/OraclePdfParser.Tests/OraclePdfParser.Tests.csproj -c Release
dotnet build src/OraclePdfParser.Api/OraclePdfParser.Api.csproj -c Release
cd web && npm install && npm run build
```

CI tests generated/sample PDFs and deterministic Arabic/financial cases. Actual uploaded files cannot be published in GitHub Actions because they contain identifiable employee data.

## Current limits

- A successful CI run **does not establish accuracy on the three confidential PDFs**. Run the private verifier before trusting results for payroll use.
- Complex multicolumn PDF output may contain mangled text (including broken totals). This must remain reviewable instead of guessed.
- The first iteration provides client-side JSON/CSV export; **SQL Server persistence, historical import identity, roles and approval workflow are not yet implemented**.
- Designed for local use. Do not expose the API over the internet without authentication, TLS, audit logging and access controls.

See `.github/workflows/ci.yml` for build automation.
