using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc;

#region Настройка ASP.NET приложения

var builder = WebApplication.CreateBuilder(args);

// Строка подключения Redis: предпочтительно из переменной окружения REDIS_CONNECTION, по умолчанию localhost
var redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";

// Добавляем сервисы Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Redis Demo API",
        Version = "v1",
        Description = "Демонстрационное ASP.NET 9 приложение с Redis",
        Contact = new OpenApiContact
        {
            Name = "OTUS",
            Url = new Uri("https://otus.ru")

        }
    });
});

// ConnectionMultiplexer как singleton
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnection));

// Добавляем сервис-помощник
builder.Services.AddSingleton<RedisService>();

// Добавляем IDistributedCache на основе Redis
builder.Services.AddStackExchangeRedisCache(options => { options.Configuration = redisConnection; });

// Фоновая служба подписчика для демонстрации pub/sub
builder.Services.AddHostedService<RedisSubscriber>();

var app = builder.Build();

// Настраиваем конвейер middleware
if (app.Environment.IsDevelopment())
{
    // Включаем Swagger UI в development-окружении
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Redis Demo API v1");
        c.RoutePrefix = string.Empty; // Делаем Swagger UI доступным по корневому пути
        c.DocumentTitle = "Redis Demo API Documentation";
    });
}
else
{
    // В production можно использовать Swagger по другому пути
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Redis Demo API v1");
        c.RoutePrefix = "swagger"; // Доступ по /swagger
    });
}

#endregion

#region API Endpoints

#region Распределённый кэш IDistributedCache

app.MapGet("/distcache/{key}", async (string key, IDistributedCache cache) =>
{
    var bytes = await cache.GetAsync(key);
    if (bytes is null) return Results.NotFound();
    var txt = System.Text.Encoding.UTF8.GetString(bytes);
    return Results.Ok(new { key, value = txt });
})
.WithName("GetDistCache")
.WithOpenApi(operation => new(operation)
{
    Summary = "Применяем IDistributedCache"
});

app.MapPost("/distcache/{key}", async (string key, HttpRequest req, IDistributedCache cache) =>
{
    using var sr = new StreamReader(req.Body);
    var body = await sr.ReadToEndAsync();
    var opts = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
    await cache.SetStringAsync(key, body, opts);
    return Results.Ok(new { key });
})
.WithName("SetDistCache")
.WithOpenApi(operation => new(operation)
{
    Summary = "Применяем IDistributedCache",
    Description = "Добавление тела запроса в кэш через интерфейс IDistributedCache"
});

#endregion

#region Демонстрация использования Lazy Loading кэширования
app.MapGet("/products/{id}", async (string id, RedisService redis) =>
{
    var data = await redis.GetOrSetAsync($"products:{id}", async () =>
         {
             // Имитация получения данных из базы данных или внешнего сервиса
             await Task.Delay(5000);
             return new { Id = id, Company = "OTUS", Name = "Курс ASP.NET разработчик", Mentor = "andrey@aprogrammer.ru", Duration = "6 месяцев", Timestamp = DateTime.UtcNow };
         }, TimeSpan.FromMinutes(5));

    return Results.Ok(data);
})
.WithName("Products")
.WithOpenApi(operation => new(operation)
{
    Summary = "Инфо о продукте",
    Description = "Подходит для кэширования результатов длительных операций, например, фасетного поиска по каталогу продуктов"
});

#endregion

#region Простое ключ/значение с использованием StackExchange.Redis напрямую
app.MapGet("/cache/{key}", async (string key, RedisService redis) =>
{
    var val = await redis.GetStringAsync(key);
    return val is null ? Results.NotFound() : Results.Ok(new { key, value = val });
})
.WithName("GetCache")
.WithOpenApi(operation => new(operation)
{
    Summary = "Получить значение из кэша",
    Description = "Извлекает значение по ключу из Redis"
});

app.MapPost("/cache/{key}", async (string key, HttpRequest req, RedisService redis) =>
{
    using var sr = new StreamReader(req.Body);
    var body = await sr.ReadToEndAsync();
    // опциональный параметр ttl в секундах
    var ttlSeconds = int.TryParse(req.Query["ttl"], out var t) ? t : 60;
    await redis.SetStringAsync(key, body, TimeSpan.FromSeconds(ttlSeconds));
    return Results.Ok(new { key, ttlSeconds });
})
.WithName("SetCache")
.WithOpenApi(operation => new(operation)
{
    Summary = "Сохранить значение в кэш",
    Description = "Сохраняет значение в Redis с указанным TTL"
});

#endregion

#region Пример счётчика
app.MapPost("/counter/increment", async (RedisService redis) =>
{
    var newVal = await redis.IncrementAsync("demo:counter");
    return Results.Ok(new { counter = newVal });
})
.WithName("IncrementCounter")
.WithOpenApi(operation => new(operation)
{
    Summary = "Распределённый счётчик",
    Description = "Атомарно увеличивает значение счётчика в Redis на 1"
});
#endregion

#region Распределённые блокировки: захват и освобождение
app.MapPost("/lock/{key}", async (string key, LockRequest req, RedisService redis) =>
{
    var ttl = TimeSpan.FromSeconds(req?.TtlSeconds ?? 10);
    var token = await redis.AcquireLockWithTokenAsync(key, ttl);
    if (token is null) return Results.Conflict(new { message = "не удалось установить блокировку" });
    return Results.Ok(new { key, token });
})
.WithName("AcquireLock")
.WithOpenApi(operation => new(operation)
{
    Summary = "Установить блокировку",
    Description = "Пытается установить распределённую блокировку с указанным TTL"
});

app.MapPost("/lock/{key}/release", async (string key, ReleaseLockRequest req, RedisService redis) =>
{
    if (req is null || string.IsNullOrEmpty(req.Token)) return Results.BadRequest();
    var released = await redis.ReleaseLockAsync(key, req.Token);
    return Results.Ok(new { key, released });
})
.WithName("ReleaseLock")
.WithOpenApi(operation => new(operation)
{
    Summary = "Освободить блокировку",
    Description = "Освобождает распределённую блокировку с проверкой токена"
});
#endregion

#region Брокер сообщений
app.MapPost("/publish", async (PublishRequest req, RedisService redis) =>
{
    var channel = string.IsNullOrWhiteSpace(req.Channel) ? RedisSubscriber.Channel : req.Channel;
    await redis.PublishAsync(channel, JsonSerializer.Serialize(new { message = req.Message ?? "", data = req.data ?? new { } }));
    return Results.Ok(new { channel });
})
.WithName("PublishMessage")
.WithOpenApi(operation => new(operation)
{
    Summary = "Брокер сообщений",
    Description = "Публикует сообщение в указанный канал"
});
#endregion

#region Очередь 
app.MapGet("/queue", async (RedisService redis) =>
{
    var message = await redis.DequeueAsync<dynamic>("demo_queue");
    if (message is null)
    {
        return Results.NoContent();
    }

    return Results.Ok(message);
})
.WithName("Dequeue")
.WithOpenApi(operation => new(operation)
{
    Summary = "Прочитать сообщение из очереди (demo_queue)"
});

app.MapPost("/queue", async ([FromBody] string message, RedisService redis) =>
{
    await redis.EnqueueAsync("demo_queue", new { Message = message, Timestamp = DateTime.UtcNow });
    return Results.Ok("Message enqueued.");
})
.WithName("Enqueue")
.WithOpenApi(operation => new(operation)
{
    Summary = "Добавить сообщение в очередь (demo_queue)"
});
#endregion

// Простой endpoint для проверки работоспособности
app.MapGet("/", () => Results.Text("Демо ASP.NET (.NET 9) + Redis - смотри README для доступных endpoints\nSwagger UI доступен по пути /swagger"));

#endregion

app.Run();

// DTOs
public record PublishRequest(string? Channel, string? Message, object? data);
public record LockRequest(int? TtlSeconds);
public record ReleaseLockRequest(string Token);