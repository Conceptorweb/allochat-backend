using Microsoft.EntityFrameworkCore;

public static class GroupEndpoints
{
    private const int MaxGroupNameLength = 40;
    private const int MaxGroupMessageLength = 2000;
    private const int MaxGroupMembers = 50;

    public static void MapGroupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/groups/create", async (CreateGroupRequest request, AlloChatDbContext db) =>
        {
            var cleanName = CleanText(request.Name, MaxGroupNameLength);
            var cleanCreatorUserID = CleanID(request.CreatorUserID);

            if (string.IsNullOrWhiteSpace(cleanName))
            {
                return Results.BadRequest(new StandardServerResponse(false, "Group name is required."));
            }

            if (string.IsNullOrWhiteSpace(cleanCreatorUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "CreatorUserID is required."));
            }

            var creatorExists = await db.Users.AnyAsync(u => u.UserID == cleanCreatorUserID);

            if (!creatorExists)
            {
                return Results.NotFound(new StandardServerResponse(false, "Creator profile not found."));
            }

            var cleanMembers = (request.Members ?? new List<GroupMemberDTO>())
                .Where(m => !string.IsNullOrWhiteSpace(m.UserID))
                .Select(m => new GroupMemberDTO(
                    CleanID(m.UserID),
                    CleanText(m.DisplayName, 60),
                    string.IsNullOrWhiteSpace(m.AvatarImageData) ? null : m.AvatarImageData.Trim()
                ))
                .Where(m => !string.IsNullOrWhiteSpace(m.UserID))
                .GroupBy(m => m.UserID)
                .Select(g => g.First())
                .Take(MaxGroupMembers)
                .ToList();

            if (!cleanMembers.Any(m => m.UserID == cleanCreatorUserID))
            {
                cleanMembers.Insert(0, new GroupMemberDTO(cleanCreatorUserID, "Creator", null));
            }

            if (cleanMembers.Count < 2)
            {
                return Results.BadRequest(new StandardServerResponse(false, "At least two group members are required."));
            }

            var memberIDs = cleanMembers.Select(m => m.UserID).Distinct().ToList();
            var existingUserIDs = await db.Users
                .Where(u => memberIDs.Contains(u.UserID))
                .Select(u => u.UserID)
                .ToListAsync();

            var validMembers = cleanMembers
                .Where(m => existingUserIDs.Contains(m.UserID))
                .GroupBy(m => m.UserID)
                .Select(g => g.First())
                .ToList();

            if (!validMembers.Any(m => m.UserID == cleanCreatorUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "Creator must be a valid group member."));
            }

            if (validMembers.Count < 2)
            {
                return Results.BadRequest(new StandardServerResponse(false, "At least one valid recipient is required."));
            }

            var groupID = Guid.NewGuid().ToString().ToLowerInvariant();
            var now = DateTime.UtcNow;

            db.Groups.Add(new GroupEntity
            {
                GroupID = groupID,
                Name = cleanName,
                CreatorUserID = cleanCreatorUserID,
                IsDeleted = false,
                CreatedAt = now
            });

            foreach (var member in validMembers)
            {
                db.GroupMembers.Add(new GroupMemberEntity
                {
                    GroupMemberID = Guid.NewGuid().ToString().ToLowerInvariant(),
                    GroupID = groupID,
                    UserID = member.UserID,
                    DisplayName = string.IsNullOrWhiteSpace(member.DisplayName) ? "Member" : member.DisplayName,
                    AvatarImageData = string.IsNullOrWhiteSpace(member.AvatarImageData) ? null : member.AvatarImageData.Trim()
                });
            }

            await db.SaveChangesAsync();

            Console.WriteLine($"GROUP_CREATE groupID={groupID} creator={ShortID(cleanCreatorUserID)} members={validMembers.Count}");

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
            var cleanGroupID = CleanID(request.GroupID).ToLowerInvariant();
            var cleanSenderID = CleanID(request.SenderID);
            var cleanSenderName = CleanText(request.SenderName, 60);
            var cleanContent = CleanText(request.Content, MaxGroupMessageLength);

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

            var isMember = await IsGroupMemberAsync(db, cleanGroupID, cleanSenderID);

            if (!isMember)
            {
                Console.WriteLine($"SECURITY_BLOCK group_send non_member groupID={ShortID(cleanGroupID)} userID={ShortID(cleanSenderID)}");
                return Results.BadRequest(new StandardServerResponse(false, "Sender is not a member of this group."));
            }

            var message = new GroupMessageEntity
            {
                MessageID = Guid.NewGuid().ToString().ToLowerInvariant(),
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
            var pushSuccessCount = 0;
            var pushFailureCount = 0;

            foreach (var device in receiverDevices)
            {
                var pushResult = await pushService.SendMessageNotificationAsync(
                    deviceToken: device.Token,
                    title: $"Group message from {message.SenderName}",
                    body: cleanContent.Length > 120 ? cleanContent.Substring(0, 117) + "..." : cleanContent
                );

                if (pushResult.Success)
                {
                    pushSuccessCount += 1;
                }
                else
                {
                    pushFailureCount += 1;
                    Console.WriteLine($"GROUP_APNS_FAIL status={pushResult.StatusCode} token={ShortToken(device.Token)} body={TrimForLog(pushResult.ResponseBody)}");

                    if (pushResult.StatusCode == 400 || pushResult.StatusCode == 410)
                    {
                        device.IsActive = false;
                    }
                }
            }

            if (receiverDevices.Any(d => !d.IsActive))
            {
                await db.SaveChangesAsync();
            }

            Console.WriteLine($"GROUP_SEND groupID={ShortID(cleanGroupID)} receivers={receiverUserIDs.Count} devices={receiverDevices.Count} pushOK={pushSuccessCount} pushFail={pushFailureCount}");

            return Results.Ok(new StandardServerResponse(true, "Message sent."));
        })
        .WithName("SendGroupMessage")
        .WithOpenApi();

        app.MapGet("/api/groups/messages/{groupID}", async (string groupID, string? requestingUserID, AlloChatDbContext db) =>
        {
            var cleanGroupID = CleanID(groupID).ToLowerInvariant();
            var cleanRequestingUserID = CleanID(requestingUserID);

            if (string.IsNullOrWhiteSpace(cleanGroupID) || string.IsNullOrWhiteSpace(cleanRequestingUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID and RequestingUserID are required."));
            }

            var isMember = await IsGroupMemberAsync(db, cleanGroupID, cleanRequestingUserID);

            if (!isMember)
            {
                Console.WriteLine($"SECURITY_BLOCK group_messages non_member groupID={ShortID(cleanGroupID)} userID={ShortID(cleanRequestingUserID)}");
                return Results.BadRequest(new StandardServerResponse(false, "You are not a member of this group."));
            }

            var messages = await db.GroupMessages
                .Where(m => m.GroupID == cleanGroupID)
                .OrderBy(m => m.SentAt)
                .Take(500)
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
            var cleanUserID = CleanID(userID);

            if (string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "UserID is required."));
            }

            var userExists = await db.Users.AnyAsync(u => u.UserID == cleanUserID);

            if (!userExists)
            {
                return Results.NotFound(new StandardServerResponse(false, "User not found."));
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

        app.MapGet("/api/groups/{groupID}/members", async (string groupID, string? requestingUserID, AlloChatDbContext db) =>
        {
            var cleanGroupID = CleanID(groupID).ToLowerInvariant();
            var cleanRequestingUserID = CleanID(requestingUserID);

            if (string.IsNullOrWhiteSpace(cleanGroupID) || string.IsNullOrWhiteSpace(cleanRequestingUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID and RequestingUserID are required."));
            }

            var isMember = await IsGroupMemberAsync(db, cleanGroupID, cleanRequestingUserID);

            if (!isMember)
            {
                Console.WriteLine($"SECURITY_BLOCK group_members non_member groupID={ShortID(cleanGroupID)} userID={ShortID(cleanRequestingUserID)}");
                return Results.BadRequest(new StandardServerResponse(false, "You are not a member of this group."));
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
            var cleanGroupID = CleanID(request.GroupID).ToLowerInvariant();
            var cleanUserID = CleanID(request.UserID);

            if (string.IsNullOrWhiteSpace(cleanGroupID) || string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new StandardServerResponse(false, "GroupID and UserID are required."));
            }

            var group = await db.Groups.FirstOrDefaultAsync(g => g.GroupID == cleanGroupID);

            if (group == null)
            {
                return Results.NotFound(new StandardServerResponse(false, "Group not found."));
            }

            if (group.CreatorUserID == cleanUserID && !group.IsDeleted)
            {
                return Results.BadRequest(new StandardServerResponse(false, "The creator must delete the group instead of leaving it."));
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

            Console.WriteLine($"GROUP_LEAVE groupID={ShortID(cleanGroupID)} userID={ShortID(cleanUserID)}");

            return Results.Ok(new StandardServerResponse(true, "Left group."));
        })
        .WithName("LeaveGroup")
        .WithOpenApi();

        app.MapPost("/api/groups/delete", async (DeleteGroupRequest request, AlloChatDbContext db) =>
        {
            var cleanGroupID = CleanID(request.GroupID).ToLowerInvariant();
            var cleanRequestingUserID = CleanID(request.RequestingUserID);

            if (string.IsNullOrWhiteSpace(cleanGroupID) || string.IsNullOrWhiteSpace(cleanRequestingUserID))
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
                Console.WriteLine($"SECURITY_BLOCK group_delete not_creator groupID={ShortID(cleanGroupID)} userID={ShortID(cleanRequestingUserID)}");
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

            db.GroupMessages.Add(new GroupMessageEntity
            {
                MessageID = Guid.NewGuid().ToString().ToLowerInvariant(),
                GroupID = cleanGroupID,
                SenderID = cleanRequestingUserID,
                SenderName = "System",
                Content = $"[GROUP_DELETED]\nThis group was deleted by {creatorName}. It can now be viewed only.",
                SentAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();

            Console.WriteLine($"GROUP_DELETE groupID={ShortID(cleanGroupID)} creator={ShortID(cleanRequestingUserID)}");

            return Results.Ok(new StandardServerResponse(true, "Group deleted."));
        })
        .WithName("DeleteGroup")
        .WithOpenApi();


app.MapGet("/api/admin/stats/groups", async (AlloChatDbContext db) =>
{
    var groupsCount = await db.Groups.CountAsync();

    return Results.Ok(new
    {
        groupsCount
    });
})
.WithName("GetGroupsStats")
.WithOpenApi();

    }

    private static async Task<bool> IsGroupMemberAsync(AlloChatDbContext db, string groupID, string userID)
    {
        return await db.GroupMembers.AnyAsync(gm => gm.GroupID == groupID && gm.UserID == userID);
    }

    private static async Task<List<DeviceEntity>> GetActiveDevicesForGroupUserIDsAsync(AlloChatDbContext db, List<string> userIDs)
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

    private static string CleanID(string? value)
    {
        return value?.Trim() ?? "";
    }

    private static string CleanText(string? value, int maxLength)
    {
        var clean = value?.Trim() ?? "";
        return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength);
    }

    private static string ShortID(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "empty"; }
        return value.Length <= 8 ? value : value.Substring(0, 8);
    }

    private static string ShortToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "empty"; }
        return value.Length <= 10 ? "***" : $"{value.Substring(0, 4)}...{value.Substring(value.Length - 4)}";
    }

    private static string TrimForLog(string value)
    {
        var clean = value?.Replace("\n", " ").Replace("\r", " ").Trim() ?? "";
        return clean.Length <= 160 ? clean : clean.Substring(0, 160) + "...";
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
