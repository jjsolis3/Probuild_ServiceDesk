using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Infrastructure.Data;

public class ServiceDeskDbContext : DbContext
{
    public ServiceDeskDbContext(DbContextOptions<ServiceDeskDbContext> options)
        : base(options)
    {
    }

    // Core entities
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Asset> Assets => Set<Asset>();

    // Asset ITAM extensions
    public DbSet<AssetAssignmentHistory> AssetAssignmentHistory => Set<AssetAssignmentHistory>();
    public DbSet<AssetAuditLog> AssetAuditLogs => Set<AssetAuditLog>();
    public DbSet<AssetCredential> AssetCredentials => Set<AssetCredential>();
    public DbSet<AssetAttachment> AssetAttachments => Set<AssetAttachment>();
    public DbSet<AssetRelationship> AssetRelationships => Set<AssetRelationship>();
    public DbSet<AssetMaintenanceLog> AssetMaintenanceLogs => Set<AssetMaintenanceLog>();
    public DbSet<AssetCheckout> AssetCheckouts => Set<AssetCheckout>();

    // Software licenses & consumables
    public DbSet<SoftwareLicense> SoftwareLicenses => Set<SoftwareLicense>();
    public DbSet<ConsumableItem> ConsumableItems => Set<ConsumableItem>();
    public DbSet<ConsumableTransaction> ConsumableTransactions => Set<ConsumableTransaction>();

    // Employee credential vault
    public DbSet<EmployeeCredential> EmployeeCredentials => Set<EmployeeCredential>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CompanyService> CompanyServices => Set<CompanyService>();

    // Ticket threading & attachments & audit
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<TicketEmail> TicketEmails => Set<TicketEmail>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<TicketHistory> TicketHistory => Set<TicketHistory>();

    // Knowledge base
    public DbSet<KbArticle> KbArticles => Set<KbArticle>();

    // Settings entities
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<TicketState> TicketStates => Set<TicketState>();
    public DbSet<ResolutionCode> ResolutionCodes => Set<ResolutionCode>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
    public DbSet<EmailConfiguration> EmailConfigurations => Set<EmailConfiguration>();

    // Routing & assignment
    public DbSet<AssignmentRule> AssignmentRules => Set<AssignmentRule>();

    // Automation workflow rules
    public DbSet<WorkflowRule> WorkflowRules => Set<WorkflowRule>();

    // Saved ticket view presets (per-user filter shortcuts)
    public DbSet<SavedTicketView> SavedTicketViews => Set<SavedTicketView>();

    // Agent productivity
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();

    // Ticket categorisation
    public DbSet<TicketCategoryEntry> TicketCategories => Set<TicketCategoryEntry>();
    public DbSet<TicketSubCategory> TicketSubCategories => Set<TicketSubCategory>();
    public DbSet<CategoryKeyword> CategoryKeywords => Set<CategoryKeyword>();

    // Email templates
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();

    // AI triage
    public DbSet<AiRecommendation> AiRecommendations => Set<AiRecommendation>();
    public DbSet<AiRunLog> AiRunLogs => Set<AiRunLog>();

    // Google Workspace
    public DbSet<GoogleWorkspaceSettings> GoogleWorkspaceSettings => Set<GoogleWorkspaceSettings>();

    // CSAT surveys
    public DbSet<CsatSurvey> CsatSurveys => Set<CsatSurvey>();

    // Ticket time tracking
    public DbSet<TicketTimeEntry> TicketTimeEntries => Set<TicketTimeEntry>();

    // Contractor payroll receipts
    public DbSet<PayrollReceipt> PayrollReceipts => Set<PayrollReceipt>();

    // Notification / email activity log
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    // In-app portal notifications (notification bell)
    public DbSet<PortalNotification> PortalNotifications => Set<PortalNotification>();

    // Software license seat assignments
    public DbSet<LicenseSeat> LicenseSeats => Set<LicenseSeat>();

    // Employee onboarding / offboarding checklists
    public DbSet<EmployeeTaskTemplate> EmployeeTaskTemplates => Set<EmployeeTaskTemplate>();
    public DbSet<EmployeeTask> EmployeeTasks => Set<EmployeeTask>();

    // Quarterly store
    public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
    public DbSet<StoreOrderItem> StoreOrderItems => Set<StoreOrderItem>();
    public DbSet<StoreAccessList> StoreAccessList => Set<StoreAccessList>();
    public DbSet<StoreOperationsAccess> StoreOperationsAccess => Set<StoreOperationsAccess>();
    public DbSet<StoreProductImage> StoreProductImages => Set<StoreProductImage>();
    public DbSet<StoreCartItem> StoreCartItems => Set<StoreCartItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Ticket -> SubmittedBy relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.SubmittedBy)
            .WithMany(e => e.SubmittedTickets)
            .HasForeignKey(t => t.SubmittedById)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket -> AssignedTo relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.AssignedTo)
            .WithMany(e => e.AssignedTickets)
            .HasForeignKey(t => t.AssignedToId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket -> CompanyService relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.CompanyService)
            .WithMany(s => s.RelatedTickets)
            .HasForeignKey(t => t.CompanyServiceId)
            .OnDelete(DeleteBehavior.SetNull);

        // TicketNote -> Ticket relationship
        modelBuilder.Entity<TicketNote>()
            .HasOne(n => n.Ticket)
            .WithMany(t => t.Notes)
            .HasForeignKey(n => n.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // TicketEmail -> Ticket relationship
        modelBuilder.Entity<TicketEmail>()
            .HasOne(e => e.Ticket)
            .WithMany(t => t.Emails)
            .HasForeignKey(e => e.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique index on TicketEmail.GmailMessageId (anti-duplicate)
        modelBuilder.Entity<TicketEmail>()
            .HasIndex(e => e.GmailMessageId)
            .IsUnique();

        // Index on TicketEmail.MessageId for threading lookups
        modelBuilder.Entity<TicketEmail>()
            .HasIndex(e => e.MessageId);

        // TicketAttachment -> Ticket
        modelBuilder.Entity<TicketAttachment>()
            .HasOne(a => a.Ticket)
            .WithMany(t => t.Attachments)
            .HasForeignKey(a => a.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // TicketHistory -> Ticket
        modelBuilder.Entity<TicketHistory>()
            .HasOne(h => h.Ticket)
            .WithMany(t => t.History)
            .HasForeignKey(h => h.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index on TicketHistory for fast per-ticket lookups
        modelBuilder.Entity<TicketHistory>()
            .HasIndex(h => new { h.TicketId, h.ChangedDate });

        // Asset -> AssignedTo relationship
        modelBuilder.Entity<Asset>()
            .HasOne(a => a.AssignedTo)
            .WithMany(e => e.AssignedAssets)
            .HasForeignKey(a => a.AssignedToId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique constraint on Asset Tag
        modelBuilder.Entity<Asset>()
            .HasIndex(a => a.AssetTag)
            .IsUnique();

        // AssetAssignmentHistory -> Asset
        modelBuilder.Entity<AssetAssignmentHistory>()
            .HasOne(h => h.Asset)
            .WithMany(a => a.AssignmentHistory)
            .HasForeignKey(h => h.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssetAssignmentHistory>()
            .HasOne(h => h.AssignedTo)
            .WithMany()
            .HasForeignKey(h => h.AssignedToId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AssetAssignmentHistory>()
            .HasOne(h => h.AssignedBy)
            .WithMany()
            .HasForeignKey(h => h.AssignedById)
            .OnDelete(DeleteBehavior.SetNull);

        // AssetAuditLog -> Asset
        modelBuilder.Entity<AssetAuditLog>()
            .HasOne(l => l.Asset)
            .WithMany(a => a.AuditLogs)
            .HasForeignKey(l => l.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssetAuditLog>()
            .HasIndex(l => new { l.AssetId, l.ChangedDate });

        // AssetCredential -> Asset
        modelBuilder.Entity<AssetCredential>()
            .HasOne(c => c.Asset)
            .WithMany(a => a.Credentials)
            .HasForeignKey(c => c.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssetCredential>()
            .Property(c => c.EncryptedPassword)
            .HasColumnType("nvarchar(max)");

        // AssetAttachment -> Asset
        modelBuilder.Entity<AssetAttachment>()
            .HasOne(at => at.Asset)
            .WithMany(a => a.Attachments)
            .HasForeignKey(at => at.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // AssetRelationship -> SourceAsset
        modelBuilder.Entity<AssetRelationship>()
            .HasOne(r => r.SourceAsset)
            .WithMany(a => a.RelationshipsFrom)
            .HasForeignKey(r => r.SourceAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // AssetRelationship -> TargetAsset (restrict to avoid multiple cascade paths)
        modelBuilder.Entity<AssetRelationship>()
            .HasOne(r => r.TargetAsset)
            .WithMany(a => a.RelationshipsTo)
            .HasForeignKey(r => r.TargetAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket -> Asset (optional FK)
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.Asset)
            .WithMany(a => a.RelatedTickets)
            .HasForeignKey(t => t.AssetId)
            .OnDelete(DeleteBehavior.SetNull);

        // AssetMaintenanceLog -> Asset
        modelBuilder.Entity<AssetMaintenanceLog>()
            .HasOne(m => m.Asset)
            .WithMany(a => a.MaintenanceLogs)
            .HasForeignKey(m => m.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // AssetCheckout -> Asset
        modelBuilder.Entity<AssetCheckout>()
            .HasOne(c => c.Asset)
            .WithMany(a => a.Checkouts)
            .HasForeignKey(c => c.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // AssetCheckout -> CheckedOutTo (Employee, restrict)
        modelBuilder.Entity<AssetCheckout>()
            .HasOne(c => c.CheckedOutTo)
            .WithMany()
            .HasForeignKey(c => c.CheckedOutToId)
            .OnDelete(DeleteBehavior.SetNull);

        // ConsumableTransaction -> ConsumableItem
        modelBuilder.Entity<ConsumableTransaction>()
            .HasOne(t => t.ConsumableItem)
            .WithMany(i => i.Transactions)
            .HasForeignKey(t => t.ConsumableItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint on Employee Email
        modelBuilder.Entity<Employee>()
            .HasIndex(e => e.Email)
            .IsUnique();

        // Branch -> SiteManager relationship
        modelBuilder.Entity<Branch>()
            .HasOne(b => b.SiteManager)
            .WithMany()
            .HasForeignKey(b => b.SiteManagerId)
            .OnDelete(DeleteBehavior.SetNull);

        // PortalUser -> Employee relationship
        modelBuilder.Entity<PortalUser>()
            .HasOne(p => p.Employee)
            .WithMany()
            .HasForeignKey(p => p.EmployeeId)
            .OnDelete(DeleteBehavior.SetNull);

        // PortalUser -> Role relationship
        modelBuilder.Entity<PortalUser>()
            .HasOne(p => p.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique constraint on PortalUser Email
        modelBuilder.Entity<PortalUser>()
            .HasIndex(p => p.Email)
            .IsUnique();

        // UserGroupMember -> UserGroup relationship
        modelBuilder.Entity<UserGroupMember>()
            .HasOne(m => m.UserGroup)
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.UserGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // UserGroupMember -> PortalUser relationship
        modelBuilder.Entity<UserGroupMember>()
            .HasOne(m => m.PortalUser)
            .WithMany(p => p.GroupMemberships)
            .HasForeignKey(m => m.PortalUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint on UserGroupMember (no duplicate memberships)
        modelBuilder.Entity<UserGroupMember>()
            .HasIndex(m => new { m.UserGroupId, m.PortalUserId })
            .IsUnique();

        // Unique constraint on AppSetting Key
        modelBuilder.Entity<AppSetting>()
            .HasIndex(s => s.Key)
            .IsUnique();

        // EmailConfiguration -> DefaultAssignee relationship
        modelBuilder.Entity<EmailConfiguration>()
            .HasOne(e => e.DefaultAssignee)
            .WithMany()
            .HasForeignKey(e => e.DefaultAssigneeId)
            .OnDelete(DeleteBehavior.SetNull);

        // Employee -> Branch relationship
        modelBuilder.Entity<Employee>()
            .HasOne(e => e.Branch)
            .WithMany(b => b.Employees)
            .HasForeignKey(e => e.BranchId)
            .OnDelete(DeleteBehavior.SetNull);

        // Decimal precision for contractor hourly rate
        modelBuilder.Entity<Employee>()
            .Property(e => e.HourlyRate)
            .HasPrecision(10, 2);

        // Ticket -> Branch relationship (location snapshot)
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.Branch)
            .WithMany()
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.SetNull);

        // AssignmentRule -> Branch (optional)
        modelBuilder.Entity<AssignmentRule>()
            .HasOne(r => r.Branch)
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.SetNull);

        // AssignmentRule -> Assignee
        modelBuilder.Entity<AssignmentRule>()
            .HasOne(r => r.Assignee)
            .WithMany()
            .HasForeignKey(r => r.AssigneeId)
            .OnDelete(DeleteBehavior.Restrict);

        // AssignmentRule -> SubCategory (optional)
        modelBuilder.Entity<AssignmentRule>()
            .HasOne(r => r.SubCategory)
            .WithMany()
            .HasForeignKey(r => r.SubCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for fast rule lookup
        modelBuilder.Entity<AssignmentRule>()
            .HasIndex(r => new { r.IsActive, r.SortOrder });

        // KbArticle -> SourceTicket relationship (optional)
        modelBuilder.Entity<KbArticle>()
            .HasOne(k => k.SourceTicket)
            .WithMany()
            .HasForeignKey(k => k.SourceTicketId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for KB search by category + published status
        modelBuilder.Entity<KbArticle>()
            .HasIndex(k => new { k.IsPublished, k.Category });

        // SavedTicketView -> Owner (PortalUser)
        modelBuilder.Entity<SavedTicketView>()
            .HasOne(v => v.Owner)
            .WithMany()
            .HasForeignKey(v => v.OwnerPortalUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // SavedTicketView -> FilterBranch
        modelBuilder.Entity<SavedTicketView>()
            .HasOne(v => v.FilterBranch)
            .WithMany()
            .HasForeignKey(v => v.FilterBranchId)
            .OnDelete(DeleteBehavior.SetNull);

        // SavedTicketView -> FilterGroup
        modelBuilder.Entity<SavedTicketView>()
            .HasOne(v => v.FilterGroup)
            .WithMany()
            .HasForeignKey(v => v.FilterGroupId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index: fast lookup of a user's views + shared views
        modelBuilder.Entity<SavedTicketView>()
            .HasIndex(v => new { v.OwnerPortalUserId, v.IsShared });

        // EmployeeCredential -> Employee
        modelBuilder.Entity<EmployeeCredential>()
            .HasOne(c => c.Employee)
            .WithMany(e => e.Credentials)
            .HasForeignKey(c => c.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EmployeeCredential>()
            .Property(c => c.EncryptedPassword)
            .HasColumnType("nvarchar(max)");

        // Subscription -> Asset (optional)
        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.Asset)
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .OnDelete(DeleteBehavior.SetNull);

        // Decimal precision — prevents silent truncation on SQL Server
        modelBuilder.Entity<Asset>()
            .Property(a => a.PurchaseCost)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Subscription>()
            .Property(s => s.AnnualCost)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Subscription>()
            .Property(s => s.MonthlyCost)
            .HasPrecision(18, 2);

        // Store sanitized HTML email body without length limit
        modelBuilder.Entity<Ticket>()
            .Property(t => t.DescriptionHtml)
            .HasColumnType("nvarchar(max)");

        // Decimal precision — prevents silent truncation and suppresses EF warnings
        modelBuilder.Entity<AssetMaintenanceLog>()
            .Property(m => m.Cost)
            .HasPrecision(18, 2);

        modelBuilder.Entity<ConsumableItem>()
            .Property(i => i.UnitCost)
            .HasPrecision(18, 2);

        modelBuilder.Entity<SoftwareLicense>()
            .Property(l => l.CostPerSeat)
            .HasPrecision(18, 2);

        // Ticket -> SubCategory (optional)
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.SubCategory)
            .WithMany()
            .HasForeignKey(t => t.SubCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Ticket -> UserGroup (group assignment, optional)
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.UserGroup)
            .WithMany()
            .HasForeignKey(t => t.UserGroupId)
            .OnDelete(DeleteBehavior.SetNull);

        // TicketCategoryEntry — no IDENTITY; IDs 0-7 are seeded as system categories
        modelBuilder.Entity<TicketCategoryEntry>()
            .Property(c => c.Id)
            .ValueGeneratedNever();

        // Index on SubCategory for fast lookup by parent category
        modelBuilder.Entity<TicketSubCategory>()
            .HasIndex(s => new { s.Category, s.IsActive, s.SortOrder });

        // Index on CategoryKeyword for fast lookup by category
        modelBuilder.Entity<CategoryKeyword>()
            .HasIndex(k => new { k.Category, k.IsActive });

        // Unique index on EmailTemplate Key
        modelBuilder.Entity<EmailTemplate>()
            .HasIndex(t => t.Key)
            .IsUnique();

        // Store email body without length limit
        modelBuilder.Entity<EmailTemplate>()
            .Property(t => t.BodyTemplate)
            .HasColumnType("nvarchar(max)");

        // AiRecommendation -> Ticket (cascade)
        modelBuilder.Entity<AiRecommendation>()
            .HasOne(r => r.Ticket)
            .WithMany()
            .HasForeignKey(r => r.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // AiRecommendation -> SuggestedAssignee (set null)
        modelBuilder.Entity<AiRecommendation>()
            .HasOne(r => r.SuggestedAssignee)
            .WithMany()
            .HasForeignKey(r => r.SuggestedAssigneeId)
            .OnDelete(DeleteBehavior.SetNull);

        // AiRecommendation -> SuggestedSubCategory (set null)
        modelBuilder.Entity<AiRecommendation>()
            .HasOne(r => r.SuggestedSubCategory)
            .WithMany()
            .HasForeignKey(r => r.SuggestedSubCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // CsatSurvey -> Ticket (cascade)
        modelBuilder.Entity<CsatSurvey>()
            .HasOne(s => s.Ticket)
            .WithMany()
            .HasForeignKey(s => s.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CsatSurvey>()
            .HasIndex(s => s.Token)
            .IsUnique();

        // Store draft reply without length limit
        modelBuilder.Entity<AiRecommendation>()
            .Property(r => r.AiDraftReply)
            .HasColumnType("nvarchar(max)");

        // Index on ticket + status for fast pending lookup
        modelBuilder.Entity<AiRecommendation>()
            .HasIndex(r => new { r.TicketId, r.Status });

        // TicketTimeEntry -> Ticket (cascade)
        modelBuilder.Entity<TicketTimeEntry>()
            .HasOne(e => e.Ticket)
            .WithMany(t => t.TimeEntries)
            .HasForeignKey(e => e.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TicketTimeEntry>()
            .HasOne(e => e.LoggedByEmployee)
            .WithMany()
            .HasForeignKey(e => e.LoggedByEmployeeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TicketTimeEntry>()
            .Property(e => e.Hours)
            .HasPrecision(6, 2);

        modelBuilder.Entity<TicketTimeEntry>()
            .HasIndex(e => new { e.TicketId, e.WorkDate });

        // TicketTimeEntry -> PayrollReceipt (set null — releasing an entry doesn't delete the receipt)
        modelBuilder.Entity<TicketTimeEntry>()
            .HasOne(e => e.PayrollReceipt)
            .WithMany(r => r.TimeEntries)
            .HasForeignKey(e => e.PayrollReceiptId)
            .OnDelete(DeleteBehavior.SetNull);

        // PayrollReceipt -> Contractor (Employee, restrict — SQL Server forbids
        // multiple cascade paths to the same table, and ApprovedById already uses SET NULL.
        // Contractors with payroll history shouldn't be hard-deleted anyway).
        modelBuilder.Entity<PayrollReceipt>()
            .HasOne(r => r.Contractor)
            .WithMany()
            .HasForeignKey(r => r.ContractorId)
            .OnDelete(DeleteBehavior.Restrict);

        // PayrollReceipt -> ApprovedBy (Employee, set null — restrict would block cascade from Employee)
        modelBuilder.Entity<PayrollReceipt>()
            .HasOne(r => r.ApprovedBy)
            .WithMany()
            .HasForeignKey(r => r.ApprovedById)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<PayrollReceipt>()
            .Property(r => r.TotalHours)
            .HasPrecision(10, 2);

        modelBuilder.Entity<PayrollReceipt>()
            .Property(r => r.TotalBillableHours)
            .HasPrecision(10, 2);

        modelBuilder.Entity<PayrollReceipt>()
            .Property(r => r.HourlyRateSnapshot)
            .HasPrecision(10, 2);

        modelBuilder.Entity<PayrollReceipt>()
            .Property(r => r.TotalAmount)
            .HasPrecision(12, 2);

        modelBuilder.Entity<PayrollReceipt>()
            .HasIndex(r => new { r.ContractorId, r.Status });

        // LicenseSeat -> SoftwareLicense (cascade)
        modelBuilder.Entity<LicenseSeat>()
            .HasOne(s => s.SoftwareLicense)
            .WithMany(l => l.Seats)
            .HasForeignKey(s => s.SoftwareLicenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LicenseSeat>()
            .HasOne(s => s.Employee)
            .WithMany(e => e.LicenseSeats)
            .HasForeignKey(s => s.EmployeeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<LicenseSeat>()
            .HasOne(s => s.Asset)
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<LicenseSeat>()
            .HasIndex(s => new { s.SoftwareLicenseId, s.RevokedDate });

        // EmployeeTask -> Employee (cascade)
        modelBuilder.Entity<EmployeeTask>()
            .HasOne(t => t.Employee)
            .WithMany(e => e.Tasks)
            .HasForeignKey(t => t.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EmployeeTask>()
            .HasIndex(t => new { t.EmployeeId, t.TaskType, t.Status });

        modelBuilder.Entity<EmployeeTaskTemplate>()
            .HasIndex(t => new { t.TaskType, t.IsActive, t.SortOrder });

        // StoreOrder -> PortalUser
        modelBuilder.Entity<StoreOrder>()
            .HasOne(o => o.PortalUser)
            .WithMany()
            .HasForeignKey(o => o.PortalUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // StoreOrder -> Branch (nullable; SET NULL on branch delete so historical
        // orders survive, with the snapshot column preserving the name).
        modelBuilder.Entity<StoreOrder>()
            .HasOne(o => o.Branch)
            .WithMany()
            .HasForeignKey(o => o.BranchId)
            .OnDelete(DeleteBehavior.SetNull);

        // StoreOrderItem -> StoreOrder
        modelBuilder.Entity<StoreOrderItem>()
            .HasOne(i => i.StoreOrder)
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.StoreOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreOrderItem -> StoreProduct (restrict so products aren't deleted while ordered)
        modelBuilder.Entity<StoreOrderItem>()
            .HasOne(i => i.StoreProduct)
            .WithMany(p => p.OrderItems)
            .HasForeignKey(i => i.StoreProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StoreOrderItem>()
            .Property(i => i.UnitPriceSnapshot)
            .HasPrecision(18, 2);

        modelBuilder.Entity<StoreProduct>()
            .Property(p => p.Price)
            .HasPrecision(18, 2);

        // StoreAccessList -> PortalUser
        modelBuilder.Entity<StoreAccessList>()
            .HasOne(a => a.PortalUser)
            .WithMany()
            .HasForeignKey(a => a.PortalUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreAccessList -> GrantedBy (restrict to avoid multiple cascade paths)
        modelBuilder.Entity<StoreAccessList>()
            .HasOne(a => a.GrantedBy)
            .WithMany()
            .HasForeignKey(a => a.GrantedByPortalUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StoreAccessList>()
            .HasIndex(a => new { a.PortalUserId, a.IsActive });

        // StoreOperationsAccess -> PortalUser
        modelBuilder.Entity<StoreOperationsAccess>()
            .HasOne(a => a.PortalUser)
            .WithMany()
            .HasForeignKey(a => a.PortalUserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StoreOperationsAccess>()
            .HasOne(a => a.GrantedBy)
            .WithMany()
            .HasForeignKey(a => a.GrantedByPortalUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StoreOperationsAccess>()
            .HasIndex(a => new { a.PortalUserId, a.IsActive });

        // StoreProductImage -> StoreProduct (cascade delete)
        modelBuilder.Entity<StoreProductImage>()
            .HasOne(i => i.StoreProduct)
            .WithMany(p => p.Images)
            .HasForeignKey(i => i.StoreProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StoreProductImage>()
            .HasIndex(i => i.StoreProductId);

        modelBuilder.Entity<StoreOrder>()
            .HasIndex(o => new { o.PortalUserId, o.Year, o.Quarter });

        // WorkflowRule — no FK relationships; conditions/actions stored as JSON text
        modelBuilder.Entity<WorkflowRule>()
            .HasIndex(r => new { r.IsActive, r.Trigger, r.SortOrder });
    }
}
