namespace ServiceDesk.Core;

/// <summary>
/// Well-known placeholder Employee records seeded by DbInitializer, identified
/// by a reserved email address rather than a dedicated flag column so both the
/// seed step and any code that needs to find them (Settings dropdowns, etc.)
/// can agree on identity without an extra join.
/// </summary>
public static class SystemEmployees
{
    /// <summary>
    /// Fallback "submitter" for inbound emails that don't match a known
    /// Employee (marketing/vendor mail, unrecognized senders) — keeps that
    /// clutter from being attributed to whichever real staff member happens
    /// to be configured as the mailbox's Default Assignee.
    /// </summary>
    public const string UnassignedSenderEmail = "unassigned-sender@servicesphere.internal";
}
