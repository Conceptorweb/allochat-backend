using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger actif aussi sous IIS / Production
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

// Stockage temporaire en mémoire pour la V1 de test
var users = new ConcurrentDictionary<string, RegisteredUser>();

var messages = new ConcurrentBag<ChatMessage>();

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

// ✅ Première vraie route AlloChat
app.MapPost("/api/users/register", (RegisterUserRequest request) =>
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
    var alloCode = GenerateAlloCode(users);

    var user = new RegisteredUser(
        userID,
        alloCode,
        request.FirstName.Trim(),
        request.LastName.Trim(),
        request.AvatarSystemName?.Trim() ?? "",
        request.Availability?.Trim() ?? "Available",
        DateTime.UtcNow
    );

    users[userID] = user;

    var response = new RegisterUserResponse(
        user.UserID,
        user.AlloCode,
        user.FirstName,
        user.LastName,
        user.AvatarSystemName,
        user.Availability
    );

    return Results.Ok(response);
})
.WithName("RegisterUser")
.WithOpenApi();



app.MapPost("/api/users/lookup", (ContactLookupRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.AlloCode))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "AlloCode is required."
        ));
    }

    var user = users.Values.FirstOrDefault(u =>
        u.AlloCode.Equals(request.AlloCode.Trim(), StringComparison.OrdinalIgnoreCase)
    );

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
        user.AvatarSystemName,
        user.Availability
    ));
})
.WithName("LookupContact")
.WithOpenApi();



app.MapPost("/api/messages/send", (SendMessageRequest request) =>
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

    var message = new ChatMessage(
        Guid.NewGuid().ToString(),
        request.SenderID,
        request.ReceiverID,
        request.Content,
        DateTime.UtcNow,
        false
    );

    messages.Add(message);

    return Results.Ok(new SendMessageResponse(
        true,
        "Message sent."
    ));
})
.WithName("SendMessage")
.WithOpenApi();


app.MapGet("/api/messages/pending/{userID}", (string userID) =>
{
    if (string.IsNullOrWhiteSpace(userID))
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "UserID is required."
        ));
    }

    var pending = messages
        .Where(m => m.ReceiverID == userID && !m.Delivered)
        .OrderBy(m => m.SentAt)
        .Select(m => new PendingMessageItem(
            m.MessageID,
            m.SenderID,
            m.ReceiverID,
            m.Content,
            m.SentAt
        ))
        .ToList();

    return Results.Ok(new PendingMessagesResponse(pending));
})
.WithName("GetPendingMessages")
.WithOpenApi();


app.MapPost("/api/messages/acknowledge", (AcknowledgeMessageRequest request) =>
{
    if (request.MessageIDs == null || !request.MessageIDs.Any())
    {
        return Results.BadRequest(new StandardServerResponse(
            false,
            "No message IDs provided."
        ));
    }

    int updatedCount = 0;

    foreach (var msg in messages)
    {
        if (request.MessageIDs.Contains(msg.MessageID) && !msg.Delivered)
        {
            // comme ConcurrentBag ne permet pas modification directe,
            // on recrée le message marqué comme Delivered
            var updated = msg with { Delivered = true };
            messages.Add(updated);
            updatedCount++;
        }
    }

    return Results.Ok(new StandardServerResponse(
        true,
        $"{updatedCount} messages acknowledged."
    ));
})
.WithName("AcknowledgeMessages")
.WithOpenApi();


app.Run("http://0.0.0.0:5098");

// -------- Helpers --------

static string GenerateAlloCode(ConcurrentDictionary<string, RegisteredUser> users)
{
    while (true)
    {
        var code = "ALLO" + Random.Shared.Next(100000, 999999);
        var exists = users.Values.Any(u => u.AlloCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            return code;
    }
}

// -------- Models --------

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record RegisterUserRequest(
    string FirstName,
    string LastName,
    string AvatarSystemName,
    string Availability
);

record RegisterUserResponse(
    string UserID,
    string AlloCode,
    string FirstName,
    string LastName,
    string AvatarSystemName,
    string Availability
);

record StandardServerResponse(
    bool Success,
    string Message
);

record RegisteredUser(
    string UserID,
    string AlloCode,
    string FirstName,
    string LastName,
    string AvatarSystemName,
    string Availability,
    DateTime CreatedAt
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

record ChatMessage(
    string MessageID,
    string SenderID,
    string ReceiverID,
    string Content,
    DateTime SentAt,
    bool Delivered
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
    string AvatarSystemName,
    string Availability
);