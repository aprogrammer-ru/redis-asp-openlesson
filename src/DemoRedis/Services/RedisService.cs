using System.Security.Cryptography;
using System.Text.Json;
using StackExchange.Redis;

public class RedisService
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;

    public RedisService(IConnectionMultiplexer mux)
    {
        _mux = mux;
        _db = mux.GetDatabase();
    }

    // Операции get/set для строк
    public Task<string?> GetStringAsync(string key) => _db.StringGetAsync(key).ContinueWith(t => (string?)t.Result);

    public Task SetStringAsync(string key, string value, TimeSpan? expiry = null) => _db.StringSetAsync(key, value, expiry);

    // Кэширование данных по шаблону ленивой загрузки (Lazy Loading)
    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> getData, TimeSpan? expiry = null)
    {
        var cachedData = await _db.StringGetAsync(key);
        if (!cachedData.IsNull)
        {
            return JsonSerializer.Deserialize<T>(cachedData);
        }

        var data = await getData(); //выполнение длительной операции обработки данных (например, фасетный поиск по каталогу)
        await _db.StringSetAsync(key, JsonSerializer.Serialize(data), expiry ?? TimeSpan.FromMinutes(15));

        return data;
    }
    
    // Инкремент счётчика
    public Task<long> IncrementAsync(string key) => _db.StringIncrementAsync(key);
    
    // Блокировка без возможности снятия вручную, только по TTL
    public async Task<bool> AcquireLockAsync(string key, TimeSpan ttl)
    {
        var token = GenerateToken();
        var acquired = await _db.LockTakeAsync(key, token, ttl);
        return acquired ? true : false;
    }

    // Блокировка возвращающая токен для последующего освобождения
    public async Task<string?> AcquireLockWithTokenAsync(string key, TimeSpan ttl)
    {
        var token = GenerateToken();
        var acquired = await _db.LockTakeAsync(key, token, ttl);
        return acquired ? token : null;
    }

    // Освобождение блокировки с проверкой токена
    public async Task<bool> ReleaseLockAsync(string key, string token)
    {
        return await _db.LockReleaseAsync(key, token);
    }

    // Публикация сообщения в брокер сообщений (IoT)
    public Task<long> PublishAsync(string channel, string message) => _mux.GetSubscriber().PublishAsync(channel, message);

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes);
    }
}