using ServiceDesk.Core.Enums;

namespace ServiceDesk.Web.Models;

/// <summary>Represents a single parsed row from a CSV/TSV during employee import.</summary>
public class ImportEmployeeRow
{
    public int RowNumber { get; set; }

    public string FirstName  { get; set; } = "";
    public string LastName   { get; set; } = "";
    public string Email      { get; set; } = "";
    public string? Phone     { get; set; }
    public string? Department{ get; set; }
    public string? JobTitle  { get; set; }
    public bool IsActive     { get; set; } = true;
    public DateTime HireDate { get; set; }
    public int? BranchId     { get; set; }
    public string? SiteRaw   { get; set; }
    public string? RoleName  { get; set; }
    public int? RoleId        { get; set; }

    // Import decision
    public bool CanImport          { get; set; }
    public bool WillCreateLogin    { get; set; }
    public bool LoginAlreadyExists { get; set; }
    public string? SkipReason      { get; set; }
}

/// <summary>One Google Workspace user surfaced in the import preview, with our
/// decision about whether they can be imported into the local Employees table.</summary>
public class GoogleImportRow
{
    public string Email      { get; set; } = "";
    public string FirstName  { get; set; } = "";
    public string LastName   { get; set; } = "";
    public string FullName   { get; set; } = "";
    public string? JobTitle  { get; set; }
    public string? Department{ get; set; }
    public string? Phone     { get; set; }
    public string? OrgUnit   { get; set; }
    public bool Suspended    { get; set; }

    /// <summary>True when the email already maps to an existing Employee — row is skipped.</summary>
    public bool AlreadyExists      { get; set; }
    /// <summary>True when a portal login with this email already exists — we'll only
    /// create the Employee record and link it.</summary>
    public bool LoginAlreadyExists { get; set; }
}

/// <summary>Represents a single parsed row from a SolarWinds TSV/CSV export during ticket import.</summary>
public class ImportTicketRow
{
    public int RowNumber { get; set; }

    // Display-only originals (for preview table)
    public string OriginalTitle    { get; set; } = "";
    public string RequesterRaw     { get; set; } = "";
    public string AssigneeEmailRaw { get; set; } = "";
    public string? SiteRaw         { get; set; }

    // Mapped values ready for DB insert
    public string Title           { get; set; } = "";
    public string Description     { get; set; } = "";
    public TicketStatus   Status   { get; set; }
    public TicketPriority Priority { get; set; }
    public int Category { get; set; }
    public int?  SubCategoryId    { get; set; }
    public int?  SubmittedById    { get; set; }
    public int?  AssignedToId     { get; set; }
    public int?  BranchId         { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public DateTime? UpdatedDate  { get; set; }
    public DateTime? DueDate      { get; set; }
    public DateTime? ResolvedDate { get; set; }
    public DateTime? ClosedDate   { get; set; }
    public string? ResolutionNotes { get; set; }

    // Import decision
    public bool    CanImport        { get; set; }
    public bool    RequesterMatched { get; set; }  // false = will use placeholder employee
    public string? SkipReason       { get; set; }
}
