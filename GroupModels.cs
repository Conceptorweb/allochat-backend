using System;

public class GroupEntity
{
    public string GroupID { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatorUserID { get; set; } = "";
    public bool IsDeleted { get; set; } = false;
    public DateTime CreatedAt { get; set; }
}

public class GroupMemberEntity
{
    public string GroupMemberID { get; set; } = "";
    public string GroupID { get; set; } = "";
    public string UserID { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarImageData { get; set; }
}

public class GroupMessageEntity
{
    public string MessageID { get; set; } = "";
    public string GroupID { get; set; } = "";
    public string SenderID { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAt { get; set; }
}