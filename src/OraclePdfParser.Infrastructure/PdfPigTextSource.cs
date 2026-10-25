using OraclePdfParser.Application;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
namespace OraclePdfParser.Infrastructure;
public sealed class PdfPigTextSource:IPdfTextSource {
    public IReadOnlyList<string> ReadPages(Stream stream) {
        using var memory=new MemoryStream();stream.CopyTo(memory);
        memory.Position=0;
        using var doc=PdfDocument.Open(memory);
        var output=new List<string>();
        foreach(var page in doc.GetPages())output.Add(ContentOrderTextExtractor.GetText(page));
        return output;
    }
}