using Microsoft.EntityFrameworkCore;

public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/groups/create", async (CreateGroupRequest request, AlloChatDbContext db) =>
        {
            var cleanName = request.Name?.Trim() ?? "";
var cleanCreatorUserID = request.CreatorUserID?.Trim() ?? "";

if (string.IsNullOrWhiteSpace(cleanName))
{
    return Results.BadRequest(new StandardServerResponse(false, "Group name is required."));
}

if (string.IsNullOrWhiteSpace(cleanCreatorUserID))
{
    return Results.BadRequest(new StandardServerResponse(false, "CreatorUserID is required."));
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
    CreatorUserID = cleanCreatorUserID,
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

    var group = await db.Groups.FirstOrDefaultAsync(g => g.GroupID == cleanGroupID);

if (group == null)
{
    return Results.NotFound(new StandardServerResponse(false, "Group not found."));
}

if (group.IsDeleted)
{
    return Results.BadRequest(new StandardServerResponse(false, "This group has been deleted. It can now be viewed only."));
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

Console.WriteLine($"GROUP PUSH DEBUG groupID={cleanGroupID}");
Console.WriteLine($"GROUP PUSH DEBUG receiverUserIDs={receiverUserIDs.Count}");
Console.WriteLine($"GROUP PUSH DEBUG receiverDevices={receiverDevices.Count}");

foreach (var device in receiverDevices)
{
    Console.WriteLine($"GROUP PUSH DEBUG sending to deviceID={device.DeviceID} tokenLength={device.Token.Length}");

    var pushResult = await pushService.SendMessageNotificationAsync(
        deviceToken: device.Token,
        title: $"Group message from {message.SenderName}",
        body: cleanContent.Length > 120 ? cleanContent.Substring(0, 117) + "..." : cleanContent
    );

Console.WriteLine($"GROUP APNS RESULT success={pushResult.Success} status={pushResult.StatusCode} body={pushResult.ResponseBody}");

    Console.WriteLine($"GROUP PUSH DEBUG result success={pushResult.Success} status={pushResult.StatusCode} body={pushResult.ResponseBody}");

    if (!pushResult.Success && (pushResult.StatusCode == 400 || pushResult.StatusCode == 410))
    {
        device.IsActive = false;
    }
}

    if (receiverDevices.Any(d => !d.IsActive))
    {
        await db.SaveChangesAsync();
    }

    var activeDeviceCount = receiverDevices.Count;

return Results.Ok(new StandardServerResponse(
    true,
    $"Message sent. GROUP_PUSH_V3 receivers={receiverUserIDs.Count} devices={activeDeviceCount}"
));

})
.WithName("SendGroupMessage")
.WithOpenApi();

        app.MapGet("/api/groups/messages/{groupID}", async (string groupID, AlloChatDbContext db) =>
        {
            var cleanGroupID = groupID?.Trim().ToLowerInvariant() ?? "";

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

    var groupIDs = await db.GroupMembers
        .Where(gm => gm.UserID == cleanUserID)
        .Select(gm => gm.GroupID)
        .Distinct()
        .ToListAsync();

    if (!groupIDs.Any())
    {
        return Results.Ok(new List<UserGroupResponse>());
    }

    var groups = await db.Groups
        .Where(g => groupIDs.Contains(g.GroupID))
        .OrderByDescending(g => g.CreatedAt)
        .Select(g => new UserGroupResponse(
    g.GroupID,
    g.Name,
    g.CreatorUserID,
    g.IsDeleted,
    g.CreatedAt
))
        .ToListAsync();

    return Results.Ok(groups);
})
.WithName("GetGroupsForUser")
.WithOpenApi();

        app.MapGet("/api/groups/{groupID}/members", async (string groupID, AlloChatDbContext db) =>
        {
            var cleanGroupID = groupID?.Trim().ToLowerInvariant() ?? "";

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
            var cleanGroupID = request.GroupID?.Trim().ToLowerInvariant() ?? "";
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


app.MapPost("/api/groups/delete", async (DeleteGroupRequest request, AlloChatDbContext db) =>
{
    var cleanGroupID = request.GroupID?.Trim().ToLowerInvariant() ?? "";
    var cleanRequestingUserID = request.RequestingUserID?.Trim() ?? "";

    if (string.IsNullOrWhiteSpace(cleanGroupID) ||
        string.IsNullOrWhiteSpace(cleanRequestingUserID))
    {
        return Results.BadRequest(new StandardServerResponse(false, "GroupID and RequestingUserID are required."));
    }

    var group = await db.Groups.FirstOrDefaultAsync(g => g.GroupID == cleanGroupID);

    if (group == null)
    {
        return Results.NotFound(new StandardServerResponse(false, "Group not found."));
    }

    if (group.CreatorUserID != cleanRequestingUserID)
    {
        return Results.BadRequest(new StandardServerResponse(false, "Only the group creator can delete this group."));
    }

    if (group.IsDeleted)
    {
        return Results.Ok(new StandardServerResponse(true, "Group already deleted."));
    }

    var creatorMember = await db.GroupMembers.FirstOrDefaultAsync(gm =>
        gm.GroupID == cleanGroupID &&
        gm.UserID == cleanRequestingUserID
    );

    var creatorName = string.IsNullOrWhiteSpace(creatorMember?.DisplayName)
        ? "the creator"
        : creatorMember.DisplayName.Trim();

    group.IsDeleted = true;

    var deletedMessage = new GroupMessageEntity
    {
        MessageID = Guid.NewGuid().ToString(),
        GroupID = cleanGroupID,
        SenderID = cleanRequestingUserID,
        SenderName = "System",
        Content = $"[GROUP_DELETED]\nThis group was deleted by {creatorName}. It can now be viewed only.",
        SentAt = DateTime.UtcNow
    };

    db.GroupMessages.Add(deletedMessage);

    await db.SaveChangesAsync();

    return Results.Ok(new StandardServerResponse(true, "Group deleted."));
})
.WithName("DeleteGroup")
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

public record CreateGroupRequest(string Name, string CreatorUserID, List<GroupMemberDTO> Members);

public record CreateGroupResponse(string GroupID);
public record GroupMemberDTO(string UserID, string DisplayName, string? AvatarImageData);
public record SendGroupMessageRequest(string GroupID, string SenderID, string SenderName, string Content);
public record GroupMessageResponse(string MessageID, string GroupID, string SenderID, string SenderName, string Content, DateTime SentAt);
public record UserGroupResponse(string GroupID, string Name, string CreatorUserID, bool IsDeleted, DateTime CreatedAt);
public record LeaveGroupRequest(string GroupID, string UserID);
public record DeleteGroupRequest(string GroupID, string RequestingUserID);

