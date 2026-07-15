using Valenius.Backend;

var builder = WebApplication.CreateBuilder(args);
builder.AddOssServices();
var app = builder.Build();
await app.InitialiseOssAsync();
app.UseOssPipeline();
app.Run();
