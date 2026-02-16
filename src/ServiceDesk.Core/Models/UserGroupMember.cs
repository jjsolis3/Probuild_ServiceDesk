namespace ServiceDesk.Core.Models;

public class UserGroupMember
{
    public int Id { get; set; }

    public int UserGroupId { get; set; }
    public int PortalUserId { get; set; }

    public DateTime JoinedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public UserGroup? UserGroup { get; set; }
    public PortalUser? PortalUser { get; set; }
}
