using OraclePdfParser.Domain;
namespace OraclePdfParser.Application;
public interface IPdfTextSource { IReadOnlyList<string> ReadPages(Stream stream); }
public interface IPayrollImportService { ImportReport Import(string fileName, IReadOnlyList<string> pages); }
