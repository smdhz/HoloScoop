using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HoloScoop.Services.Redis;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddRedisTaskIntake(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisStreamOptions>(configuration.GetSection(RedisStreamOptions.SectionName));
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisStreamOptions>>().Value;
            options.Validate();
            var redisConfiguration = ConfigurationOptions.Parse(options.ConnectionString);
            redisConfiguration.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisConfiguration);
        });
        services.AddScoped<IIncomingTaskStore, EfIncomingTaskStore>();
        services.AddHostedService<RedisStreamConsumer>();
        return services;
    }
}
