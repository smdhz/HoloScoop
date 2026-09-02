using HoloScoop.Data;
using HoloScoop.Jobs;
using HoloScoop.Search;
using HoloScoop.Services.Note;
using HoloScoop.Services.Redis;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddDbContext<HoloScoopDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.EnableRetryOnFailure()));
builder.Services.AddScoped<ITaskQueries, TaskQueries>();
builder.Services.AddScoped<ITaskCommands, TaskCommands>();
builder.Services.AddMediaAndSubtitleSearch(builder.Configuration);
builder.Services.AddNoteScheduleLookup(builder.Configuration);

if (builder.Configuration.GetValue("Redis:Enabled", true))
{
    builder.Services.AddRedisTaskIntake(builder.Configuration);
}

if (builder.Configuration.GetValue("Jobs:Enabled", true))
{
    builder.Services.AddHoloScoopJobScheduling(builder.Configuration);
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
