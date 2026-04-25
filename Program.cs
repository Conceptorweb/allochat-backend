using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// PostgreSQL / Entity Framework
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrWhiteSpace(databaseUrl))
{
    throw new InvalidOperationException("DATABASE_URL environment variable is missing.");
}

var connectionString = ConvertDatabaseUrlToConnectionString(databaseUrl);

builder.Services.AddDbContext<AlloChatDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddHttpClient<ApnsPushService>();

var app = builder.Build();

// Swagger actif aussi en production
app.UseSwagger();
app.UseSwaggerUI();

// Création automatique des tables si elles n'existent pas
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlloChatDbContext>();
    db.Database.EnsureCreated();
    EnsureDeviceTokensTable(db);
}

// Endpoint de test existant
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();

    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.MapPost("/api/users/register", async (RegisterUserRequest request, AlloChatDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.FirstName) ||
        string.IsNullOrWhiteSpace(request.LastName))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "First name and last name are required."
        ));
    }

    var userID = Guid.NewGuid().ToString();
    var alloCode = await GenerateAlloCode(db);

    var user = new RegisteredUserEntity
    {
        UserID = userID,
        AlloCode = alloCode,
        FirstName = request.FirstName.Trim(),
        LastName = request.LastName.Trim(),
        Nickname = request.Nickname?.Trim() ?? "",
        PhoneNumber = request.PhoneNumber?.Trim() ?? "",
        AvatarSystemName = request.AvatarSystemName?.Trim() ?? "",
        Availability = request.Availability?.Trim() ?? "Available",
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var response = new RegisterUserResponse(
        user.UserID,
        user.AlloCode,
        user.FirstName,
        user.LastName,
        user.Nickname,
        user.PhoneNumber,
        user.AvatarSystemName,
        user.Availability
    );

    return Results.Ok(response);
})
.WithName("RegisterUser")
.WithOpenApi();

app.MapPost("/api/users/lookup", async (ContactLookupRequest request, AlloChatDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.AlloCode))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "AlloCode is required."
        ));
    }

    var cleanCode = request.AlloCode.Trim().ToUpperInvariant();

    var user = await db.Users.FirstOrDefaultAsync(u => u.AlloCode.ToUpper() == cleanCode);

    if (user == null)
    {
        return Results.NotFound(new StandardServerResponse(
            false,
            "Contact not found."
        ));
    }

    return Results.Ok(new ContactLookupResponse(
        user.UserID,
        user.AlloCode,
        $"{user.FirstName} {user.LastName}".Trim(),
        user.Nickname,
        user.PhoneNumber,
        user.AvatarSystemName,
        user.Availability
    ));
})
.WithName("LookupContact")
.WithOpenApi();

app.MapPost("/api/devices/register", async (RegisterDeviceRequest request, AlloChatDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.UserID) ||
        string.IsNullOrWhiteSpace(request.DeviceToken))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "UserID and DeviceToken are required."
        ));
    }

    var userExists = await db.Users.AnyAsync(u => u.UserID == request.UserID);

    if (!userExists)
    {
        return Results.NotFound(new StandardServerResponse(
            false,
            "User not found."
        ));
    }

    var cleanToken = request.DeviceToken.Trim();
    var cleanPlatform = string.IsNullOrWhiteSpace(request.Platform)
        ? "watchOS"
        : request.Platform.Trim();

    var existingToken = await db.DeviceTokens
        .FirstOrDefaultAsync(t => t.Token == cleanToken);

    if (existingToken == null)
    {
        var deviceToken = new DeviceTokenEntity
        {
            DeviceTokenID = Guid.NewGuid().ToString(),
            UserID = request.UserID,
            Token = cleanToken,
            Platform = cleanPlatform,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        };

        db.DeviceTokens.Add(deviceToken);
    }
    else
    {
        existingToken.UserID = request.UserID;
        existingToken.Platform = cleanPlatform;
        existingToken.IsActive = true;
        existingToken.LastSeenAt = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new StandardServerResponse(
        true,
        "Device token registered."
    ));
})
.WithName("RegisterDeviceToken")
.WithOpenApi();

app.MapPost("/api/messages/send", async (
    SendMessageRequest request,
    AlloChatDbContext db,
    ApnsPushService pushService
) =>
{
    if (string.IsNullOrWhiteSpace(request.SenderID) ||
        string.IsNullOrWhiteSpace(request.ReceiverID) ||
        string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "Invalid message data."
        ));
    }

    var sender = await db.Users.FirstOrDefaultAsync(u => u.UserID == request.SenderID);
    var receiverExists = await db.Users.AnyAsync(u => u.UserID == request.ReceiverID);

    if (sender == null || !receiverExists)
    {
        return Results.NotFound(new StandardServerResponse(
            false,
            "Sender or receiver not found."
        ));
    }

    var message = new ChatMessageEntity
    {
        MessageID = Guid.NewGuid().ToString(),
        SenderID = request.SenderID,
        ReceiverID = request.ReceiverID,
        Content = request.Content.Trim(),
        SentAt = DateTime.UtcNow,
        Delivered = false
    };

    db.Messages.Add(message);
    await db.SaveChangesAsync();

    var receiverTokens = await db.DeviceTokens
        .Where(t => t.UserID == request.ReceiverID && t.IsActive)
        .ToListAsync();

    var senderName = BuildDisplayName(sender);

    foreach (var token in receiverTokens)
    {
        var pushResult = await pushService.SendMessageNotificationAsync(
            deviceToken: token.Token,
            title: senderName,
            body: message.Content
        );

        if (!pushResult.Success &&
            (pushResult.StatusCode == 400 || pushResult.StatusCode == 410))
        {
            token.IsActive = false;
        }
    }

    if (receiverTokens.Any(t => !t.IsActive))
    {
        await db.SaveChangesAsync();
    }

    return Results.Ok(new SendMessageResponse(
        true,
        "Message sent."
    ));
})
.WithName("SendMessage")
.WithOpenApi();

app.MapGet("/api/messages/pending/{userID}", async (string userID, AlloChatDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(userID))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "UserID is required."
        ));
    }

    var pending = await db.Messages
        .Where(m => m.ReceiverID == userID && !m.Delivered)
        .OrderBy(m => m.SentAt)
        .Select(m => new PendingMessageItem(
            m.MessageID,
            m.SenderID,
            m.ReceiverID,
            m.Content,
            m.SentAt
        ))
        .ToListAsync();

    return Results.Ok(new PendingMessagesResponse(pending));
})
.WithName("GetPendingMessages")
.WithOpenApi();

app.MapPost("/api/messages/acknowledge", async (AcknowledgeMessageRequest request, AlloChatDbContext db) =>
{
    if (request.MessageIDs == null || !request.MessageIDs.Any())
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "No message IDs provided."
        ));
    }

    var messagesToUpdate = await db.Messages
        .Where(m => request.MessageIDs.Contains(m.MessageID) && !m.Delivered)
        .ToListAsync();

    foreach (var message in messagesToUpdate)
    {
        message.Delivered = true;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new StandardServerResponse(
        true,
        $"{messagesToUpdate.Count} messages acknowledged."
    ));
})
.WithName("AcknowledgeMessages")
.WithOpenApi();

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Run($"http://0.0.0.0:{port}");

// -------- Helpers --------

static async Task<string> GenerateAlloCode(AlloChatDbContext db)
{
    while (true)
    {
        var code = "ALLO" + Random.Shared.Next(100000, 999999);
        var exists = await db.Users.AnyAsync(u => u.AlloCode == code);

        if (!exists)
        {
            return code;
        }
    }
}

static string ConvertDatabaseUrlToConnectionString(string databaseUrl)
{
    var uri = new Uri(databaseUrl);

    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

    var database = uri.AbsolutePath.TrimStart('/');

    var connectionParts = new List<string>
    {
        $"Host={uri.Host}",
        $"Database={database}",
        $"Username={username}",
        $"Password={password}",
        "Ssl Mode=Require",
        "Trust Server Certificate=true"
    };

    if (uri.Port > 0)
    {
        connectionParts.Insert(1, $"Port={uri.Port}");
    }

    return string.Join(";", connectionParts) + ";";
}

static string BuildDisplayName(RegisteredUserEntity user)
{
    if (!string.IsNullOrWhiteSpace(user.Nickname))
    {
        return user.Nickname.Trim();
    }

    var fullName = $"{user.FirstName} {user.LastName}".Trim();

    return string.IsNullOrWhiteSpace(fullName) ? "AlloChat" : fullName;
}

static void EnsureDeviceTokensTable(AlloChatDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "DeviceTokens" (
            "DeviceTokenID" text NOT NULL,
            "UserID" text NOT NULL,
            "Token" text NOT NULL,
            "Platform" text NOT NULL,
            "IsActive" boolean NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "LastSeenAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_DeviceTokens" PRIMARY KEY ("DeviceTokenID")
        );
    """);

    db.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_DeviceTokens_Token"
        ON "DeviceTokens" ("Token");
    """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_DeviceTokens_UserID"
        ON "DeviceTokens" ("UserID");
    """);
}

static string Base64UrlEncode(byte[] input)
{
    return Convert.ToBase64String(input)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

// -------- APNs --------

class ApnsPushService
{
    private readonly HttpClient _httpClient;

    public ApnsPushService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ApnsSendResult> SendMessageNotificationAsync(
        string deviceToken,
        string title,
        string body
    )
    {
        var keyID = Environment.GetEnvironmentVariable("APNS_KEY_ID") ?? "";
        var teamID = Environment.GetEnvironmentVariable("APNS_TEAM_ID") ?? "";
        var bundleID = Environment.GetEnvironmentVariable("APNS_BUNDLE_ID") ?? "";
        var privateKey = Environment.GetEnvironmentVariable("APNS_PRIVATE_KEY") ?? "";
        var apnsEnv = Environment.GetEnvironmentVariable("APNS_ENV") ?? "production";

        if (string.IsNullOrWhiteSpace(keyID) ||
            string.IsNullOrWhiteSpace(teamID) ||
            string.IsNullOrWhiteSpace(bundleID) ||
            string.IsNullOrWhiteSpace(privateKey))
        {
            return new ApnsSendResult(false, 0, "Missing APNs environment variables.");
        }

        try
        {
            var jwt = CreateProviderToken(
                keyID: keyID,
                teamID: teamID,
                privateKeyPem: privateKey
            );

            var host = apnsEnv.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
                ? "https://api.sandbox.push.apple.com"
                : "https://api.push.apple.com";

            var url = $"{host}/3/device/{deviceToken}";

            var payload = new
            {
                aps = new
                {
                    alert = new
                    {
                        title,
                        body
                    },
                    sound = "default"
                }
            };

            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Version = new Version(2, 0),
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
            request.Headers.TryAddWithoutValidation("apns-topic", bundleID);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            return new ApnsSendResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                responseBody
            );
        }
        catch (Exception ex)
        {
            return new ApnsSendResult(false, 0, ex.Message);
        }
    }

    private static string CreateProviderToken(
        string keyID,
        string teamID,
        string privateKeyPem
    )
    {
        var cleanPrivateKey = privateKeyPem.Replace("\\n", "\n");

        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["kid"] = keyID
        });

        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var claimsJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = teamID,
            ["iat"] = iat
        });

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var claims = Base64UrlEncode(Encoding.UTF8.GetBytes(claimsJson));
        var unsignedToken = $"{header}.{claims}";

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(cleanPrivateKey);

        var signatureBytes = ecdsa.SignData(
            Encoding.UTF8.GetBytes(unsignedToken),
            HashAlgorithmName.SHA256
        );

        var signature = Base64UrlEncode(signatureBytes);

        return $"{unsignedToken}.{signature}";
    }
}

record ApnsSendResult(
    bool Success,
    int StatusCode,
    string ResponseBody
);

// -------- Database --------

class AlloChatDbContext : DbContext
{
    public AlloChatDbContext(DbContextOptions<AlloChatDbContext> options) : base(options)
    {
    }

    public DbSet<RegisteredUserEntity> Users => Set<RegisteredUserEntity>();
    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();
    public DbSet<DeviceTokenEntity> DeviceTokens => Set<DeviceTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegisteredUserEntity>()
            .HasKey(u => u.UserID);

        modelBuilder.Entity<RegisteredUserEntity>()
            .HasIndex(u => u.AlloCode)
            .IsUnique();

        modelBuilder.Entity<ChatMessageEntity>()
            .HasKey(m => m.MessageID);

        modelBuilder.Entity<ChatMessageEntity>()
            .HasIndex(m => m.ReceiverID);

        modelBuilder.Entity<ChatMessageEntity>()
            .HasIndex(m => m.SenderID);

        modelBuilder.Entity<DeviceTokenEntity>()
            .HasKey(t => t.DeviceTokenID);

        modelBuilder.Entity<DeviceTokenEntity>()
            .HasIndex(t => t.Token)
            .IsUnique();

        modelBuilder.Entity<DeviceTokenEntity>()
            .HasIndex(t => t.UserID);
    }
}

class RegisteredUserEntity
{
    public string UserID { get; set; } = "";
    public string AlloCode { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string AvatarSystemName { get; set; } = "";
    public string Availability { get; set; } = "Available";
    public DateTime CreatedAt { get; set; }
}

class ChatMessageEntity
{
    public string MessageID { get; set; } = "";
    public string SenderID { get; set; } = "";
    public string ReceiverID { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAt { get; set; }
    public bool Delivered { get; set; }
}

class DeviceTokenEntity
{
    public string DeviceTokenID { get; set; } = "";
    public string UserID { get; set; } = "";
    public string Token { get; set; } = "";
    public string Platform { get; set; } = "watchOS";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

// -------- Models --------

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record RegisterUserRequest(
    string FirstName,
    string LastName,
    string? Nickname,
    string? PhoneNumber,
    string? AvatarSystemName,
    string? Availability
);

record RegisterUserResponse(
    string UserID,
    string AlloCode,
    string FirstName,
    string LastName,
    string Nickname,
    string PhoneNumber,
    string AvatarSystemName,
    string Availability
);

record StandardServerResponse(
    bool Success,
    string Message
);

record SendMessageRequest(
    string SenderID,
    string ReceiverID,
    string Content
);

record SendMessageResponse(
    bool Success,
    string Message
);

record PendingMessageItem(
    string MessageID,
    string SenderID,
    string ReceiverID,
    string Content,
    DateTime SentAt
);

record PendingMessagesResponse(
    List<PendingMessageItem> Messages
);

record AcknowledgeMessageRequest(
    List<string> MessageIDs
);

record ContactLookupRequest(
    string AlloCode
);

record ContactLookupResponse(
    string UserID,
    string AlloCode,
    string DisplayName,
    string Nickname,
    string PhoneNumber,
    string AvatarSystemName,
    string Availability
);

record RegisterDeviceRequest(
    string UserID,
    string DeviceToken,
    string? Platform
);