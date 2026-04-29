using Microsoft.EntityFrameworkCore;

public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/groups/create", async (CreateGroupRequest request, AlloChatDbContext db) =>
        {
            var groupID = Guid.NewGuid().ToString();

            var group = new GroupEntity
            {
                GroupID = groupID,
                Name = request.Name,
                CreatedAt = DateTime.UtcNow
            };

            db.Add(group);

            foreach (var member in request.Members)
            {
                db.Add(new GroupMemberEntity
                {
                    GroupMemberID = Guid.NewGuid().ToString(),
                    GroupID = groupID,
                    UserID = member.UserID,
                    DisplayName = member.DisplayName,
                    AvatarImageData = member.AvatarImageData
                });
            }

            await db.SaveChangesAsync();

            return Results.Ok(new { groupID });
        });

        app.MapPost("/api/groups/send", async (SendGroupMessageRequest request, AlloChatDbContext db) =>
        {
            var message = new GroupMessageEntity
            {
                MessageID = Guid.NewGuid().ToString(),
                GroupID = request.GroupID,
                SenderID = request.SenderID,
                SenderName = request.SenderName,
                Content = request.Content,
                SentAt = DateTime.UtcNow
            };

            db.Add(message);
            await db.SaveChangesAsync();

            return Results.Ok(new StandardServerResponse(true, "Message sent"));
        });
    }
}

public record CreateGroupRequest(string Name, List<GroupMemberDTO> Members);
public record GroupMemberDTO(string UserID, string DisplayName, string? AvatarImageData);
public record SendGroupMessageRequest(string GroupID, string SenderID, string SenderName, string Content);