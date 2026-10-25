using OraclePdfParser.Infrastructure;
using Xunit;
namespace OraclePdfParser.Tests;
public class PdfiumIntegrationTests {
 [Fact] public void RejectsInvalidPdf() {
   using var input=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a pdf"));
   Assert.ThrowsAny<Exception>(()=>new PdfiumTextSource().ReadPages(input));
 }
}