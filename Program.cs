using HoloScoop.Data;
using HoloScoop.Jobs;
using HoloScoop.Search;
using HoloScoop.Services.Media;
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

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<HoloScoopDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("StartupTaskRecovery");
    var recovered = await InterruptedTaskRecovery.RequeueAsync(dbContext);
    if (recovered > 0)
    {
        logger.LogWarning(
            "Requeued {TaskCount} interrupted media task(s) during application startup.",
            recovered);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();
app.MapMediaEndpoints();
app.MapRazorPages().WithStaticAssets();

app.Run();
