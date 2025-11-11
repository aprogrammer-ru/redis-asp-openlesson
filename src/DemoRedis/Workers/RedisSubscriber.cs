using StackExchange.Redis;

public class RedisSubscriber : BackgroundService
{
    public const string Channel = "redis-asp-otus";
    private readonly IConnectionMultiplexer _mux;

    public RedisSubscriber(IConnectionMultiplexer mux)
    {
        _mux = mux;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = _mux.GetSubscriber();
        // подписка на канал demo-channel
        sub.Subscribe(Channel, (channel, value) =>
        {
            Console.WriteLine($"[RedisSubscriber] Новое cообщение из канала '{channel}': {value}");
        });

        
        return Task.CompletedTask;
    }
}
