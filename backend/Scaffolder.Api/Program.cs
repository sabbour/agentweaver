using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Temporary registration for migration generation - will be moved to proper DI setup in T019
builder.Services.AddDbContext<Scaffolder.Api.Persistence.ScaffolderDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=scaffolder.db"));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.Run();
