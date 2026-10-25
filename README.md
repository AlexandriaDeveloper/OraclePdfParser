# OraclePdfParser

ASP.NET Core 8 + Angular 18 payroll PDF importer. DDD-inspired bounded contexts: Domain, Application, Infrastructure, API.

## Run

Requires .NET 8 SDK, Node 22 and npm.

```bash
dotnet run --project src/OraclePdfParser.Api --urls http://localhost:5080
cd web && npm install && npm start
```

Visit http://localhost:4200. Upload one, two, or three PDF documents.

## Guardrails

- Never relies on fixed PDF coordinates or fixed page counts.
- Normalize Arabic presentation forms and digits.
- Uncertain data is reported as **ReviewRequired**, never silently approved.
- Payment-sheet flattened multi-column rows are **not** assigned to individual employees until validated parsing is added.
- No identity data is logged or bundled in the repository. Uploaded PDFs are processed in memory.
- This is an initial end-to-end scaffold, **not production-certified**. CI tests synthetic content. Real-file golden-master tests are required before financial use.

## TODO before production

1. Run fixture-based tests against the three original October 2026 PDFs in a private fixture environment.
2. Implement a reliable semantic table extractor for the multi-column payment sheet without fixed coordinates; validate every employee row.
3. Improve payslip line reconstruction and bank/name extraction for RTL stream variants; show raw-source audit evidence.
4. Add SQL Server persistence with EF Core migrations, import idempotency, and role-based approval.
5. Cross-reconcile each bank/group net and count across independent files and block approval on discrepancies.

## Repository organization

- Domain: payroll records, entries, review status and validation issues.
- Application: Arabic normalization, semantic parsing and import workflow.
- Infrastructure: PDF text source abstraction implemented via PdfPig.
- API: multi-file upload endpoint with limits and PDF signature check.
- web: Angular review UI.
- tests: regression tests of normalization and safe parser behavior.
