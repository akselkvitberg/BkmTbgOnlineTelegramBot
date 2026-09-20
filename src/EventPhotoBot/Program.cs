var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

app.Run();

public partial class Program { }
