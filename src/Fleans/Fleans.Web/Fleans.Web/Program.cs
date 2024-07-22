using Fleans.Application;
using Fleans.Infrastructure;
using Orleans.Serialization;
using Fleans.Web.Client.Pages;
using Fleans.Web.Components;
using System.Dynamic;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(static siloBuilder =>
{
    siloBuilder.Services.AddSerializer(serializerBuilder =>
    {
        serializerBuilder.AddNewtonsoftJsonSerializer(
            isSupported: type => type == typeof(ExpandoObject), new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
            });
    });
});

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Fleans.Web.Client._Imports).Assembly);

app.MapControllers();

app.Run();
