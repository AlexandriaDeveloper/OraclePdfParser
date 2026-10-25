using OraclePdfParser.Application;
using OraclePdfParser.Infrastructure;
var builder=WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options=>options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IPayrollImportService,PayrollImportService>();
builder.Services.AddSingleton<IPdfTextSource,PdfiumTextSource>();
builder.Services.AddSingleton<PayrollBatchReconciler>();
var path=builder.Configuration["ImportArchive:Path"]??Path.Combine(builder.Environment.ContentRootPath,"App_Data","oracle-imports.db");
builder.Services.AddSingleton<IImportArchive>(_=>new SqliteImportArchive(path));
builder.Services.AddCors(o=>o.AddPolicy("LocalAngular",p=>p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=30*1024*1024);
var app=builder.Build();
app.UseCors("LocalAngular");
app.MapGet("/api/health",()=>Results.Ok(new{status="ok"}));
app.MapGet("/api/imports",async(IImportArchive archive,int? skip,int? take,CancellationToken ct)=>Results.Ok(await archive.ListAsync(skip??0,take??20,ct)));
app.MapGet("/api/imports/{id:guid}",async(Guid id,IImportArchive archive,CancellationToken ct)=>{var report=await archive.GetAsync(id,ct);return report is null?Results.NotFound():Results.Ok(report);});
app.MapPost("/api/import",async(HttpRequest request,IPdfTextSource pdf,IPayrollImportService importer,PayrollBatchReconciler reconcile,IImportArchive archive,CancellationToken ct)=>{
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
    var completed=reconcile.Reconcile(results);
    await archive.SaveBatchAsync(completed,ct);
    return Results.Ok(completed);
});
app.Run();
public partial class Program {}
