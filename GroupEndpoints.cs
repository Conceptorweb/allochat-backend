using Microsoft.EntityFrameworkCore;

public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/groups/create", async (CreateGroupRequest request, AlloChatDbContext db) =>
        {
            var cleanName = request.Name?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanName))
            {
                return Results.BadRequest(new StandardServerResponse(false, "Group name is required."));
            }

            var cleanMembers = (request.Members ?? new List<GroupMemberDTO>())
                .Where(m => !string.IsNullOrWhiteSpace(m.UserID))
                .GroupBy(m => m.UserID.Trim())
                .Select(g => g.First())
                .ToList();

            if (!cleanMembers.Any())
            {
                return Results.BadRequest(new StandardServerResponse(false, "At least one group member is required."));
            }

            var groupID = Guid.NewGuid().ToString();

            var group = new GroupEntity
            {
                GroupID = groupID,
                Name = cleanName,
                CreatedAt = DateTime.UtcNow
            };

            db.Groups.Add(group);

            foreach (var member in cleanMembers)
            {
                db.GroupMembers.Add(new GroupMemberEntity
                {
                    GroupMemberID = Guid.NewGuid().ToString(),
                    GroupID = groupID,
                    UserID = member.UserID.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(member.DisplayName)
                        ? "Member"
                        : member.DisplayName.Trim(),
                    AvatarImageData = string.IsNullOrWhiteSpace(member.AvatarImageData)
                        ? null
                        : member.AvatarImageData.Trim()
                });
            }

            await db.SaveChangesAsync();

            return Results.Ok(new CreateGroupResponse(groupID));
        })
        .WithName("CreateGroup")
        .WithOpenApi();




        app.MapPost("/api/groups/send", async (
    SendGroupMessageRequest request,
    AlloChatDbContext db,
    ApnsPushService pushService
) =>
{
    var cleanGroupID = request.GroupID?.Trim().ToLowerInvariant() ?? "";
    var cleanSenderID = request.SenderID?.Trim() ?? "";
    var cleanSenderName = request.SenderName?.Trim() ?? "";
    var cleanContent = request.Content?.Trim() ?? "";

    if (string.IsNullOrWhiteSpace(cleanGroupID) ||
        string.IsNullOrWhiteSpace(cleanSenderID) ||
        string.IsNullOrWhiteSpace(cleanContent))
    {
        return Results.BadRequest(new StandardServerResponse(false, "Invalid group message data."));
    }

    var isMember = await db.GroupMembers.AnyAsync(gm =>
        gm.GroupID == cleanGroupID &&
        gm.UserID == cleanSenderID
    );

    if (!isMember)
    {
        return Results.BadRequest(new StandardServerResponse(false, "Sender is not a member of this group."));
    }

    var message = new GroupMessageEntity
    {
        MessageID = Guid.NewGuid().ToString(),
        GroupID = cleanGroupID,
        SenderID = cleanSenderID,
        SenderName = string.IsNullOrWhiteSpace(cleanSenderName) ? "Member" : cleanSenderName,
        Content = cleanContent,
        SentAt = DateTime.UtcNow
    };

    db.GroupMessages.Add(message);
    await db.SaveChangesAsync();

    var receiverUserIDs = await db.GroupMembers
        .Where(gm => gm.GroupID == cleanGroupID && gm.UserID != cleanSenderID)
        .Select(gm => gm.UserID)
        .Distinct()
        .ToListAsync();

    var receiverDevices = await GetActiveDevicesForGroupUserIDsAsync(db, receiverUserIDs);

    foreach (var device in receiverDevices)
    {
        var pushResult = await pushService.SendMessageNotificationAsync(
            deviceToken: device.Token,
            title: $"Group message from {message.SenderName}",
            body: cleanContent.Length > 120 ? cleanContent.Substring(0, 117) + "..." : cleanContent
        );

        if (!pushResult.Success && (pushResult.StatusCode == 400 || pushResult.StatusCode == 410))
        {
            device.IsActive = false;
        }
    }

    if (receiverDevices.Any(d => !d.IsActive))
    {
        await db.SaveChangesAsync();
    }

    return Results.Ok(new StandardServerResponse(true, "Message sent."));
})
.WithName("SendGroupMessage")
.WithOpenApi();

        app.MapGet("/api/groups/messages/{groupID}", async (string groupID, AlloChatDbContext db) =>
        {
            var cleanGroupID = groupID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanGroupID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID is required."));
            }

            var messages = await db.GroupMessages
                .Where(m => m.GroupID == cleanGroupID)
                .OrderBy(m => m.SentAt)
                .Select(m => new GroupMessageResponse(
                    m.MessageID,
                    m.GroupID,
                    m.SenderID,
                    m.SenderName,
                    m.Content,
                    m.SentAt
                ))
                .ToListAsync();

            return Results.Ok(messages);
        })
        .WithName("GetGroupMessages")
        .WithOpenApi();

        app.MapGet("/api/groups/user/{userID}", async (string userID, AlloChatDbContext db) =>
        {
            var cleanUserID = userID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "UserID is required."));
            }

            var groups = await db.GroupMembers
                .Where(gm => gm.UserID == cleanUserID)
                .Join(
                    db.Groups,
                    gm => gm.GroupID,
                    g => g.GroupID,
                    (gm, g) => new UserGroupResponse(
                        g.GroupID,
                        g.Name,
                        g.CreatedAt
                    )
                )
                .Distinct()
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync();

            return Results.Ok(groups);
        })
        .WithName("GetGroupsForUser")
        .WithOpenApi();

        app.MapGet("/api/groups/{groupID}/members", async (string groupID, AlloChatDbContext db) =>
        {
            var cleanGroupID = groupID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanGroupID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID is required."));
            }

            var members = await db.GroupMembers
                .Where(gm => gm.GroupID == cleanGroupID)
                .OrderBy(gm => gm.DisplayName)
                .Select(gm => new GroupMemberDTO(
                    gm.UserID,
                    gm.DisplayName,
                    gm.AvatarImageData
                ))
                .ToListAsync();

            return Results.Ok(members);
        })
        .WithName("GetGroupMembers")
        .WithOpenApi();

        app.MapPost("/api/groups/leave", async (LeaveGroupRequest request, AlloChatDbContext db) =>
        {
            var cleanGroupID = request.GroupID?.Trim() ?? "";
            var cleanUserID = request.UserID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanGroupID) ||
                string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID and UserID are required."));
            }

            var memberships = await db.GroupMembers
                .Where(gm => gm.GroupID == cleanGroupID && gm.UserID == cleanUserID)
                .ToListAsync();

            if (!memberships.Any())
            {
                return Results.NotFound(new StandardServerResponse(false, "Membership not found."));
            }

            db.GroupMembers.RemoveRange(memberships);
            await db.SaveChangesAsync();

            return Results.Ok(new StandardServerResponse(true, "Left group."));
        })
        

        .WithName("LeaveGroup")
        .WithOpenApi();
    }

    private static async Task<List<DeviceEntity>> GetActiveDevicesForGroupUserIDsAsync(
        AlloChatDbContext db,
        List<string> userIDs
    )
    {
        var cleanUserIDs = userIDs
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct()
            .ToList();

        if (!cleanUserIDs.Any())
        {
            return new List<DeviceEntity>();
        }

        var deviceIDs = await db.DeviceProfiles
            .Where(link => cleanUserIDs.Contains(link.UserID))
            .Select(link => link.DeviceID)
            .Distinct()
            .ToListAsync();

        if (!deviceIDs.Any())
        {
            return new List<DeviceEntity>();
        }

        return await db.Devices
            .Where(device => deviceIDs.Contains(device.DeviceID) && device.IsActive && device.Token != "")
            .GroupBy(device => device.DeviceID)
            .Select(group => group.First())
            .ToListAsync();
    }
}

public record CreateGroupRequest(string Name, List<GroupMemberDTO> Members);


public record CreateGroupResponse(string GroupID);
public record GroupMemberDTO(string UserID, string DisplayName, string? AvatarImageData);
public record SendGroupMessageRequest(string GroupID, string SenderID, string SenderName, string Content);
public record GroupMessageResponse(string MessageID, string GroupID, string SenderID, string SenderName, string Content, DateTime SentAt);
public record UserGroupResponse(string GroupID, string Name, DateTime CreatedAt);
public record LeaveGroupRequest(string GroupID, string UserID);

