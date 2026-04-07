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

    // Saved ticket view presets (per-user filter shortcuts)
    public DbSet<SavedTicketView> SavedTicketViews => Set<SavedTicketView>();

    // Agent productivity
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();

    // Ticket categorisation
    public DbSet<TicketSubCategory> TicketSubCategories => Set<TicketSubCategory>();

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

        // Ticket -> SubCategory (optional)
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.SubCategory)
            .WithMany()
            .HasForeignKey(t => t.SubCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index on SubCategory for fast lookup by parent category
        modelBuilder.Entity<TicketSubCategory>()
            .HasIndex(s => new { s.Category, s.IsActive, s.SortOrder });
    }
}
