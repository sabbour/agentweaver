using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Configuration;
using Scaffolder.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<ScaffolderOptions>(
    builder.Configuration.GetSection(ScaffolderOptions.SectionName));

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=scaffolder.db";
builder.Services.AddDbContext<ScaffolderDbContext>(options =>
    options.UseSqlite(connectionString));

// Repositories
builder.Services.AddScoped<IRunRepository, RunRepository>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IOperationalRecordRepository, OperationalRecordRepository>();

// ProblemDetails for RFC 7807 error responses
builder.Services.AddProblemDetails();

// OpenAPI / Swagger (Swashbuckle for net8.0 - native OpenApi is net9.0+)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Scaffolder API",
        Version = "v1",
        Description = "Single-agent file-editing run management API"
    });
});

var app = builder.Build();

// Ensure database is created and migrations applied on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ScaffolderDbContext>();
    await db.Database.MigrateAsync();
}

// Error handling
app.UseExceptionHandler();
app.UseStatusCodePages();

// OpenAPI
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scaffolder API v1"));
}

// Placeholder — endpoints added in T029-T031, T035, T043
// app.MapRunsEndpoints();
// app.MapStreamEndpoints();

app.Run();

// Make Program accessible for integration tests (Microsoft.AspNetCore.Mvc.Testing)
public partial class Program { }
