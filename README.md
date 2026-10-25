# Oracle PDF Parser — .NET + Angular

A privacy-conscious, Arabic RTL payroll importer for Oracle Ministry of Finance PDF reports. **DDD-inspired separation** between Domain, Application, Infrastructure and ASP.NET Core API.

## Supported report types

- **صرفية موظف**: employee identity, earnings, itemized deductions, government contributions and net pay, even if one employee occupies multiple pages.
- **كشوف صرف**: employee payment rows, partial account numbers and aggregate amounts. Ambiguous or mis-ordered rows must be treated as unresolved, not guessed.
- **كافر**: group/bank payroll summaries and employee counts, including continuation across more than two pages.

Any one of these files can be imported independently, or up to three at once. **No fixed X/Y page coordinates.** The parser uses text order and structural anchors and flags gaps. Scanned/image-only PDFs are not yet supported; there is no OCR.

## Start locally

Requires .NET SDK **10**, Node **22**, npm and access to NuGet.

```sh
dotnet run --project src/OraclePdfParser.Api --urls http://localhost:5055
cd frontend
npm install
npm start
```

Then open http://127.0.0.1:4200. Imports are written to a local SQLite database at `src/OraclePdfParser.Api/data/imports.db` (or configure `Data:SqlitePath`).

## Automated tests

```sh
dotnet test tests/OraclePdfParser.Tests
cd frontend && npm run test && npm run build
```

**Private PDF regression:** Copy the three original documents to a private local directory, then:

```sh
ORACLE_PDF_SAMPLE_DIR=/private/path dotnet test tests/OraclePdfParser.Tests
```

Real payroll PDFs **must not be committed to this public repository**. The sample suite checks 219 detailed employees, 56 cover groups, and total 2,864,042.24 EGP as observed in the supplied October 2026 examples. This regression suite may expose unsupported PDF text-order cases rather than passing; treat red tests as a blocking issue.

## Design boundaries

- A reported net is **not** necessarily gross earnings minus deductions. Employer contributions are printed in other areas of the payslip and must be reconciled as `gross earnings + employer contributions - deductions = net` for this report variant.
- `x` rows are subtotals, not additive line items.
- Codes alone are not unique payroll item identifiers: the same code can represent 2014 and 2015 allowances, or item exclusions. Preserve source label and category.
- Every entry links to a page and original text. Wrong employee matches, missing IDs, failed reconciliation, and unparsed fields are issues.
- The backend does not automatically certify or transmit payroll figures. Review blocking errors before relying on imports.

### API
`POST /api/imports` multipart file uploads; `GET /api/imports`; `GET /api/imports/{id}`; `GET /api/imports/{id}/employees/{nationalId}`.

### Security
Operate on a trusted machine. This initial local-development API has no identity/access-control layer and should **not** be exposed on a public network until authentication, authorization, rate limiting and upload scanning are added. Input PDFs are processed in memory; SQLite and CSV exports contain personal data.
