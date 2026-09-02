using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Services.Note;

public static class NoteScheduleServiceCollectionExtensions
{
    public static IServiceCollection AddNoteScheduleLookup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("NoteConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var defaultConnection = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is required to derive the Note connection.");
            var builder = new SqlConnectionStringBuilder(defaultConnection)
            {
                InitialCatalog = configuration.GetValue("NoteSchedule:Database", "Note")
            };
            connectionString = builder.ConnectionString;
        }

        services.AddDbContext<NoteScheduleDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<INoteScheduleLookup, NoteScheduleLookup>();
        services.AddSingleton<SpeakerNameCatalog>();
        return services;
    }
}
