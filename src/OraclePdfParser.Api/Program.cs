using OraclePdfParser.Application;
using OraclePdfParser.Infrastructure;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IPayrollImportService,PayrollImportService>();
builder.Services.AddSingleton<IPdfTextSource,PdfiumTextSource>();
builder.Services.AddSingleton<PayrollBatchReconciler>();
builder.Services.AddCors(o=>o.AddPolicy("LocalAngular",p=>p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=30*1024*1024);
var app=builder.Build();
app.UseCors("LocalAngular");
app.MapGet("/api/health",()=>Results.Ok(new{status="ok"}));
app.MapPost("/api/import",async(HttpRequest request,IPdfTextSource pdf,IPayrollImportService importer,PayrollBatchReconciler reconcile,CancellationToken ct)=>{
    if(!request.HasFormContentType)return Results.BadRequest(new{error="Multipart form-data required"});
    var form=await request.ReadFormAsync(ct);
    var files=form.Files;
    if(files.Count is <1 or >3)return Results.BadRequest(new{error="Upload one to three PDF files"});
    var results=new List<OraclePdfParser.Domain.ImportReport>();
    foreach(var file in files) {
        if(file.Length is <=0 or >30*1024*1024)return Results.BadRequest(new{error="Invalid file size"});
        await using var upload=file.OpenReadStream();
        var prefix=new byte[5]; if(await upload.ReadAsync(prefix,ct)!=5 || System.Text.Encoding.ASCII.GetString(prefix)!="%PDF-")
            return Results.BadRequest(new{error="Only actual PDF files are accepted"});
        using var buffer=new MemoryStream();await buffer.WriteAsync(prefix,ct);await upload.CopyToAsync(buffer,ct);
        buffer.Position=0;
        try {results.Add(importer.Import(Path.GetFileName(file.FileName),pdf.ReadPages(buffer))); }
        catch(Exception){return Results.BadRequest(new{error="PDF could not be parsed safely"});}
    }
    return Results.Ok(reconcile.Reconcile(results));
});
app.Run();
public partial class Program {}
