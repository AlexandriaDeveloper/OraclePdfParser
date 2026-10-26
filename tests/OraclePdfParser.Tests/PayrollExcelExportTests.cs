using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;
using Xunit;

namespace OraclePdfParser.Tests;

public class PayrollExcelExportTests {
    private static readonly PayrollEntry Allowance=new("21110442","بدل الجامعة",10.25m,EntryKind.Earning,1,"fixture");
    private static readonly PayrollEntry AnotherAllowance=Allowance with {Amount=5.25m};
    private static PayrollEmployee Employee(string id,string name,IReadOnlyList<PayrollEntry> entries,IReadOnlyList<string>? warnings=null)=>
        new(id,"00000000-00001",name,"بنك تجريبي",100m,40m,60m,entries,warnings??[],1,2)
        {InstitutionalEntityCode="30200105"};

    private static ImportReport Report(VerificationStatus status,params PayrollEmployee[] people)=>
        new(Guid.NewGuid(),"synthetic.pdf",ReportKind.EmployeePayslip,status,2,people,[],[])
        {Period="2026-10"};

    private static SpreadsheetDocument OpenWorkbook(byte[] bytes) =>
        SpreadsheetDocument.Open(new MemoryStream(bytes),false);

    private static WorksheetPart Sheet(SpreadsheetDocument doc,string sheetName){
        var wb=doc.WorkbookPart!;
        var sheet=wb.Workbook.Sheets!.Elements<Sheet>().Single(s=>s.Name==sheetName);
        return (WorksheetPart)wb.GetPartById(sheet.Id!.Value!);
    }
    private static IReadOnlyList<Row> Rows(WorksheetPart sheet)=>
        sheet.Worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
    private static Cell CellAt(Row row,int column)=>
        row.Elements<Cell>().ElementAt(column);

    [Fact]
    public void SelectsOnlyRequestedEmployeeAndFinancialColumns_AsNumericXlsx(){
        var person=Employee("00000000000001","=NOT_A_FORMULA",[Allowance,AnotherAllowance]);
        var excluded=Employee("00000000000002","موظف ثاني",[]);
        var report=Report(VerificationStatus.Verified,person,excluded);
        var excel=new PayrollExcelExporter().Create(report,
            new(report.Id,false,[0],true,false,true,[PayrollExcelExporter.ItemKey(Allowance)]));
        Assert.True(excel.Length>1000);
        Assert.Equal("PK",System.Text.Encoding.ASCII.GetString(excel,0,2));
        using var doc=OpenWorkbook(excel);
        Assert.Equal(3,doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Count());
        var ws=Sheet(doc,"ملخص الموظفين");
        Assert.True(ws.Worksheet.GetFirstChild<SheetViews>()!.Elements<SheetView>().Single().RightToLeft!.Value);
        var rows=Rows(ws);
        Assert.Equal(3,rows.Count); // heading, one employee, partial-available totals
        Assert.Equal("=NOT_A_FORMULA",CellAt(rows[1],0).InlineString!.Text!.Text);
        Assert.Equal(CellValues.InlineString,CellAt(rows[1],0).DataType!.Value);
        Assert.Null(CellAt(rows[1],0).CellFormula);
        Assert.Equal("00000000000001",CellAt(rows[1],1).InlineString!.Text!.Text);
        Assert.Equal("100",CellAt(rows[1],5).CellValue!.Text);
        Assert.Equal("60",CellAt(rows[1],6).CellValue!.Text);
        Assert.Equal("15.50",CellAt(rows[1],7).CellValue!.Text);
        Assert.Equal(CellValues.Number,CellAt(rows[1],7).DataType!.Value);
        Assert.Equal(2,Rows(Sheet(doc,"تفاصيل البنود")).Count-1);
        Assert.Equal("1",CellAt(Rows(Sheet(doc,"تفاصيل البنود"))[1],7).CellValue!.Text);
    }

    [Fact]
    public void MissingSelectedItemIsZeroOnlyForVerifiedEmployees(){
        var present=Employee("00000000000001","الاول",[Allowance]);
        var missing=Employee("00000000000002","الثاني",[]);
        var exporter=new PayrollExcelExporter();
        var verified=Report(VerificationStatus.Verified,present,missing);
        var key=PayrollExcelExporter.ItemKey(Allowance);
        using (var doc=OpenWorkbook(exporter.Create(verified,
            new(verified.Id,true,null,false,false,false,[key])))){
            var rows=Rows(Sheet(doc,"ملخص الموظفين"));
            Assert.Equal("0",CellAt(rows[2],5).CellValue!.Text);
            Assert.Equal(CellValues.Number,CellAt(rows[2],5).DataType!.Value);
        }
        var requiresReview=Report(VerificationStatus.ReviewRequired,present,missing);
        using (var doc=OpenWorkbook(exporter.Create(requiresReview,
            new(requiresReview.Id,true,null,false,false,false,[key])))){
            var rows=Rows(Sheet(doc,"ملخص الموظفين"));
            Assert.Null(CellAt(rows[2],5).CellValue);
            Assert.Equal(CellValues.InlineString,CellAt(rows[2],5).DataType!.Value);
        }
    }

    [Fact]
    public void RejectsForgedItemKeysAndInvalidEmployeeSelections(){
        var report=Report(VerificationStatus.Verified,Employee("00000000000001","اختبار",[Allowance]));
        var exporter=new PayrollExcelExporter();
        Assert.Throws<ArgumentException>(()=>exporter.Create(report,
            new(report.Id,false,[99],false,false,true,null)));
        Assert.Throws<ArgumentException>(()=>exporter.Create(report,
            new(report.Id,true,null,false,false,false,["Earning|99999999|مجهول"])));
        Assert.Throws<ArgumentException>(()=>exporter.Create(report,
            new(report.Id,true,null,false,false,false,[])));
    }
}
