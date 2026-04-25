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

var app = builder.Build();

// Swagger actif aussi en production
app.UseSwagger();
app.UseSwaggerUI();

// Création automatique des tables si elles n'existent pas
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlloChatDbContext>();
    db.Database.EnsureCreated();
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

app.MapPost("/api/messages/send", async (SendMessageRequest request, AlloChatDbContext db) =>
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

    var senderExists = await db.Users.AnyAsync(u => u.UserID == request.SenderID);
    var receiverExists = await db.Users.AnyAsync(u => u.UserID == request.ReceiverID);

    if (!senderExists || !receiverExists)
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

// -------- Database --------

class AlloChatDbContext : DbContext
{
    public AlloChatDbContext(DbContextOptions<AlloChatDbContext> options) : base(options)
    {
    }

    public DbSet<RegisteredUserEntity> Users => Set<RegisteredUserEntity>();
    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();

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