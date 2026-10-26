using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;

namespace OraclePdfParser.Infrastructure;

/// <summary>
/// Generate a native, numeric XLSX entirely from an archived import snapshot.
/// Never guess missing financial values or accept figures supplied by the browser.
/// </summary>
public sealed class PayrollExcelExporter {
    private const int MaxSelectedItemColumns = 500;
    private const int ExcelMaxRows = 1_048_576;
    private sealed record Column(string Caption, Func<PayrollEmployee, object?> Value, bool Money = false);
    public static string ItemKey(PayrollEntry e) => $"{e.Kind}|{e.AccountCode ?? ""}|{e.Label}";

    public byte[] Create(ImportReport report, PayrollExcelRequest request) {
        if (report.Id != request.ReportId) throw new ArgumentException("Report ID mismatch");
        if (report.Kind is not (ReportKind.EmployeePayslip or ReportKind.PaymentSheet))
            throw new ArgumentException("This report does not contain employee rows");
        var indexes=request.AllEmployees
            ? Enumerable.Range(0,report.Employees.Count).ToArray()
            : (request.EmployeeIndexes ?? []).Distinct().ToArray();
        if (indexes.Length==0 || indexes.Length>ExcelMaxRows-2 ||
            indexes.Any(i=>i<0 || i>=report.Employees.Count))
            throw new ArgumentException("Select one or more valid employees");
        var itemKeys=(request.ItemKeys ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if(itemKeys.Length>MaxSelectedItemColumns)
            throw new ArgumentException("Select at most 500 distinct payroll items");
        if(!request.IncludeGross && !request.IncludeDeductions && !request.IncludeNet && itemKeys.Length==0)
            throw new ArgumentException("Select at least one payroll amount or item");
        var choices=report.Employees.SelectMany(e=>e.Entries)
            .Where(e=>e.Kind is EntryKind.Earning or EntryKind.Deduction or EntryKind.Informational)
            .GroupBy(ItemKey, StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        if(itemKeys.Any(k=>k.Length>1000 || !choices.ContainsKey(k)))
            throw new ArgumentException("At least one selected item is not in this report");

        var employees=indexes.Select(i=>report.Employees[i]).ToArray();
        var columns=new List<Column>{
            new("اسم الموظف",e=>e.Name),new("الرقم القومي",e=>e.NationalId),
            new("رقم الصرف",e=>e.DisbursementNumber),new("كود الجهة المؤسسي",e=>e.InstitutionalEntityCode),
            new("البنك",e=>e.Bank)
        };
        if(request.IncludeGross)columns.Add(new("إجمالي الاستحقاقات",e=>e.Gross,true));
        if(request.IncludeDeductions)columns.Add(new("إجمالي الاستقطاعات",e=>e.Deductions,true));
        if(request.IncludeNet)columns.Add(new("الصافي",e=>e.Net,true));
        foreach(var k in itemKeys){
            var item=choices[k];
            var heading=$"{item.Kind switch{EntryKind.Earning=>"استحقاق",EntryKind.Deduction=>"استقطاع",_=>"معلوماتي"}} | {item.AccountCode ?? "بدون حساب"} | {item.Label}";
            columns.Add(new(heading,e=>{
                var matches=e.Entries.Where(x=>ItemKey(x)==k).ToArray();
                if(matches.Length>0)return matches.Sum(x=>x.Amount);
                // A missing detail row can only be treated as zero when the
                // complete report and this employee were successfully verified.
                return report.Status==VerificationStatus.Verified && e.Warnings.Count==0 ? 0m : null;
            },true));
        }
        columns.Add(new("حالة التقرير",_=>report.Status.ToString()));
        columns.Add(new("ملاحظات المراجعة",e=>string.Join(" | ",e.Warnings)));
        columns.Add(new("الملف المصدر",_=>report.FileName));
        columns.Add(new("صفحات المصدر",e=>$"{e.StartPage}-{e.EndPage}"));

        using var stream=new MemoryStream();
        using (var document=SpreadsheetDocument.Create(stream,SpreadsheetDocumentType.Workbook,true)){
            var wb=document.AddWorkbookPart();
            wb.Workbook=new Workbook();
            var style=wb.AddNewPart<WorkbookStylesPart>();
            style.Stylesheet=MakeStyles();
            style.Stylesheet.Save();
            var sheets=wb.Workbook.AppendChild(new Sheets());
            var summary=AddSheet(wb,sheets,"ملخص الموظفين",1,columns.Count);
            var summaryRows=summary.Worksheet.GetFirstChild<SheetData>()!;
            AddRow(summaryRows,1,columns.Select(c=>(object?)c.Caption).ToArray(),[]);
            int rowNumber=2;
            foreach(var employee in employees){
                var vals=columns.Select(c=>c.Value(employee)).ToArray();
                var moneyColumns=columns.Select((c,i)=>(c,i)).Where(x=>x.c.Money).Select(x=>x.i).ToHashSet();
                AddRow(summaryRows,rowNumber++,vals,moneyColumns);
            }
            // Totals are explicitly labeled as the sum of available numeric
            // cells, never an assertion that all source records were complete.
            var totals=new object?[columns.Count];
            totals[0]="مجموع القيم المتاحة (قد تكون غير مكتملة)";
            var monetary=columns.Select((c,i)=>(c,i)).Where(x=>x.c.Money).Select(x=>x.i).ToHashSet();
            foreach(var idx in monetary){
                decimal sum=0m;
                foreach(var e in employees)if(columns[idx].Value(e) is decimal d)sum+=d;
                totals[idx]=sum;
            }
            AddRow(summaryRows,rowNumber,totals,monetary);
            FinishSheet(summary,summaryRows,columns.Count,rowNumber);

            var detailHeaders=new[]{"اسم الموظف","الرقم القومي","رقم الصرف","نوع البند","كود الحساب","اسم البند","القيمة","صفحة المصدر","الملف المصدر","حالة التقرير"};
            var details=AddSheet(wb,sheets,"تفاصيل البنود",2,detailHeaders.Length);
            var detailRows=details.Worksheet.GetFirstChild<SheetData>()!;
            AddRow(detailRows,1,detailHeaders.Cast<object?>().ToArray(),[]);
            int detailRow=2;
            var selected=itemKeys.ToHashSet(StringComparer.Ordinal);
            foreach(var e in employees){
                foreach(var entry in e.Entries.Where(x=>
                    selected.Contains(ItemKey(x)) ||
                    (request.IncludeGross && x.Kind==EntryKind.Earning) ||
                    (request.IncludeDeductions && x.Kind==EntryKind.Deduction))){
                    if(detailRow>ExcelMaxRows)throw new ArgumentException("Too many payroll detail rows for Excel");
                    AddRow(detailRows,detailRow++,
                        [e.Name,e.NationalId,e.DisbursementNumber,entry.Kind.ToString(),entry.AccountCode,entry.Label,entry.Amount,entry.Page,report.FileName,report.Status.ToString()],
                        [6]);
                }
            }
            FinishSheet(details,detailRows,detailHeaders.Length,detailRow-1);

            var notes=AddSheet(wb,sheets,"معلومات ومراجعة",3,3);
            var noteRows=notes.Worksheet.GetFirstChild<SheetData>()!;
            var noteList=new List<object?[]>{
                new object?[]{"الحقل","القيمة","ملاحظة"},
                new object?[]{"ملف المصدر",report.FileName,null},
                new object?[]{"الشهر",report.Period,null},
                new object?[]{"نوع التقرير",report.Kind.ToString(),null},
                new object?[]{"حالة التحقق",report.Status.ToString(),"ReviewRequired تعني أن الأرقام تحتاج مراجعة"},
                new object?[]{"عدد الموظفين المختارين",employees.Length,null},
                new object?[]{"عدد البنود المختارة",itemKeys.Length,null},
                new object?[]{"تاريخ التصدير UTC",DateTimeOffset.UtcNow.ToString("O"),null},
                new object?[]{"معنى المعلوماتي","مساهمة جهة العمل/الحكومة","ليس استحقاقًا نقديًا إضافيًا للموظف"},
                new object?[]{"القيم الفارغة","غير متاحة أو غير مثبتة","لا تفسر القيم الفارغة على أنها صفر"},
                new object?[]{"الصفر في بند غير موجود","صفر في تقرير متحقق فقط","إذا لم يكن التقرير أو الموظف متحققًا تُترك الخانة فارغة"},
                new object?[]{"المجاميع","القيم الرقمية المتاحة فقط","لا تعتبر اعتمادًا للمبالغ عند وجود نقص أو تحذيرات"},
                new object?[]{"الخصوصية","يحتوي الملف بيانات مالية وشخصية","احفظه في مكان آمن ولا تشاركه دون تصريح"}
            };
            foreach(var issue in report.Issues)noteList.Add(["تحذير من التقرير",issue.Code,issue.Message]);
            foreach(var emp in employees.Where(e=>e.Warnings.Count>0))
                noteList.Add(["موظف يحتاج مراجعة",emp.Name,string.Join(" | ",emp.Warnings)]);
            if(noteList.Count>ExcelMaxRows)throw new ArgumentException("Too many review notes for Excel");
            for(int i=0;i<noteList.Count;i++)AddRow(noteRows,i+1,noteList[i],[]);
            FinishSheet(notes,noteRows,3,noteList.Count);
            wb.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static Stylesheet MakeStyles(){
        var fonts=new Fonts(new Font(),new Font(new Bold(),new Color{Rgb="FFFFFFFF"})){Count=2};
        var fills=new Fills(
            new Fill(new PatternFill{PatternType=PatternValues.None}),
            new Fill(new PatternFill{PatternType=PatternValues.Gray125}),
            new Fill(new PatternFill(new ForegroundColor{Rgb="FF16496B"}){PatternType=PatternValues.Solid})){Count=3};
        var borders=new Borders(new Border()){Count=1};
        var cellStyleFormats=new CellStyleFormats(new CellFormat()){Count=1};
        var formats=new CellFormats(
            new CellFormat{FontId=0,FillId=0,BorderId=0,FormatId=0},
            new CellFormat{FontId=1,FillId=2,BorderId=0,FormatId=0,ApplyFont=true,ApplyFill=true,ApplyAlignment=true,
                Alignment=new Alignment{Horizontal=HorizontalAlignmentValues.Right,WrapText=true}},
            new CellFormat{FontId=0,FillId=0,BorderId=0,NumberFormatId=4,ApplyNumberFormat=true}
        ){Count=3};
        return new Stylesheet(fonts,fills,borders,cellStyleFormats,formats);
    }
    private static WorksheetPart AddSheet(WorkbookPart workbook,Sheets sheets,string name,uint sheetId,int columns){
        var part=workbook.AddNewPart<WorksheetPart>();
        var cols=new Columns();
        for(uint i=1;i<=columns;i++)
            cols.Append(new DocumentFormat.OpenXml.Spreadsheet.Column{Min=i,Max=i,Width=i==1?34: i<=5?21:28,CustomWidth=true});
        part.Worksheet=new Worksheet(
            new SheetViews(new SheetView{WorkbookViewId=0,RightToLeft=true}),
            new SheetFormatProperties{DefaultRowHeight=17},
            cols,new SheetData());
        sheets.Append(new Sheet{Name=name,SheetId=sheetId,Id=workbook.GetIdOfPart(part)});
        return part;
    }
    private static void AddRow(SheetData data,int rowNumber,IReadOnlyList<object?> values,HashSet<int> moneyColumns){
        var row=new Row{RowIndex=(uint)rowNumber};
        for(int i=0;i<values.Count;i++){
            var cell=new Cell{CellReference=ColumnName(i)+(uint)rowNumber,StyleIndex=rowNumber==1?1U:moneyColumns.Contains(i)?2U:0U};
            var value=values[i];
            if(value is decimal d){
                cell.DataType=CellValues.Number;
                cell.CellValue=new CellValue(d.ToString(CultureInfo.InvariantCulture));
            }else if(value is int n){
                cell.DataType=CellValues.Number;
                cell.CellValue=new CellValue(n.ToString(CultureInfo.InvariantCulture));
            }else{
                // Inline strings, not formula cells: user-controlled employee
                // names and descriptions can never be evaluated by Excel.
                cell.DataType=CellValues.InlineString;
                cell.InlineString=new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(value?.ToString()??""){Space=SpaceProcessingModeValues.Preserve});
            }
            row.Append(cell);
        }
        data.Append(row);
    }
    private static string ColumnName(int zeroBased){
        int n=zeroBased+1;
        var letters="";
        while(n>0){n--;letters=(char)('A'+n%26)+letters;n/=26;}
        return letters;
    }
    private static void FinishSheet(WorksheetPart sheet,SheetData rows,int numberOfColumns,int lastRow){
        if(lastRow>1){
            sheet.Worksheet.Append(new AutoFilter{Reference=$"A1:{ColumnName(numberOfColumns-1)}{lastRow}"});
        }
        sheet.Worksheet.Save();
    }
}
