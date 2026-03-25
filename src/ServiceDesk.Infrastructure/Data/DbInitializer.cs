using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;

namespace ServiceDesk.Infrastructure.Data;

public static class DbInitializer
{
    /// <summary>
    /// Applies any pending schema changes that aren't covered by EF migrations.
    /// Each ALTER TABLE / CREATE TABLE is guarded by an existence check so it
    /// is safe to run on every startup.
    /// </summary>
    public static void ApplySchemaUpgrades(ServiceDeskDbContext context)
    {
        try
        {
            // 1. Add BranchId to Employees (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'BranchId'
                )
                BEGIN
                    ALTER TABLE dbo.Employees
                        ADD BranchId INT NULL
                        CONSTRAINT FK_Employees_Branches
                        REFERENCES dbo.Branches(Id)
                        ON DELETE SET NULL;
                END");

            // 2. Add BranchId to Tickets (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'BranchId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD BranchId INT NULL
                        CONSTRAINT FK_Tickets_Branches
                        REFERENCES dbo.Branches(Id)
                        ON DELETE SET NULL;
                END");

            // 3. Create AssignmentRules table (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssignmentRules')
                BEGIN
                    CREATE TABLE dbo.AssignmentRules (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name        NVARCHAR(200)   NOT NULL,
                        Category    INT             NULL,
                        BranchId    INT             NULL
                            CONSTRAINT FK_AssignmentRules_Branches
                            REFERENCES dbo.Branches(Id)
                            ON DELETE SET NULL,
                        AssigneeId  INT             NOT NULL
                            CONSTRAINT FK_AssignmentRules_Employees
                            REFERENCES dbo.Employees(Id),
                        SortOrder   INT             NOT NULL DEFAULT 100,
                        IsActive    BIT             NOT NULL DEFAULT 1,
                        CreatedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_AssignmentRules_Active_Sort
                        ON dbo.AssignmentRules (IsActive, SortOrder);
                END");

            // 4. Add DueDate to Tickets (SLA upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'DueDate'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD DueDate DATETIME2 NULL;
                END");

            // 5. Create TicketAttachments table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketAttachments')
                BEGIN
                    CREATE TABLE dbo.TicketAttachments (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId        INT             NOT NULL
                            CONSTRAINT FK_TicketAttachments_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        FileName        NVARCHAR(255)   NOT NULL,
                        StoredFileName  NVARCHAR(255)   NOT NULL,
                        ContentType     NVARCHAR(100)   NOT NULL DEFAULT '',
                        FileSize        BIGINT          NOT NULL DEFAULT 0,
                        UploadedBy      NVARCHAR(200)   NOT NULL DEFAULT '',
                        UploadedDate    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                END");

            // 6. Create TicketHistory table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketHistory')
                BEGIN
                    CREATE TABLE dbo.TicketHistory (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId    INT             NOT NULL
                            CONSTRAINT FK_TicketHistory_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        ChangedBy   NVARCHAR(200)   NOT NULL,
                        FieldName   NVARCHAR(100)   NOT NULL,
                        OldValue    NVARCHAR(500)   NULL,
                        NewValue    NVARCHAR(500)   NULL,
                        ChangedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_TicketHistory_Ticket_Date
                        ON dbo.TicketHistory (TicketId, ChangedDate);
                END");

            // 7. Add SmtpUsername / SmtpPassword to EmailConfigurations (SMTP App Password support)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations') AND name = 'SmtpUsername'
                )
                BEGIN
                    ALTER TABLE dbo.EmailConfigurations ADD SmtpUsername NVARCHAR(200) NULL;
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations') AND name = 'SmtpPassword'
                )
                BEGIN
                    ALTER TABLE dbo.EmailConfigurations ADD SmtpPassword NVARCHAR(500) NULL;
                END");

            // 8. Create KbArticles table (Knowledge Base)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'KbArticles')
                BEGIN
                    CREATE TABLE dbo.KbArticles (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Title           NVARCHAR(300)   NOT NULL,
                        Problem         NVARCHAR(4000)  NOT NULL,
                        Solution        NVARCHAR(MAX)   NOT NULL,
                        Category        INT             NOT NULL DEFAULT 0,
                        SourceTicketId  INT             NULL
                            CONSTRAINT FK_KbArticles_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE SET NULL,
                        IsPublished     BIT             NOT NULL DEFAULT 1,
                        CreatedBy       NVARCHAR(200)   NOT NULL DEFAULT '',
                        CreatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        LastUpdated     DATETIME2       NULL,
                        ViewCount       INT             NOT NULL DEFAULT 0
                    );

                    CREATE INDEX IX_KbArticles_Published_Category
                        ON dbo.KbArticles (IsPublished, Category);
                END");

            // 9. Add PasswordResetToken columns to PortalUsers (Forgot Password support)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.PortalUsers') AND name = 'PasswordResetToken'
                )
                BEGIN
                    ALTER TABLE dbo.PortalUsers
                        ADD PasswordResetToken NVARCHAR(200) NULL,
                            PasswordResetTokenExpiry DATETIME2 NULL;
                END");

            // 10. Create CannedResponses table (agent reply templates)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CannedResponses')
                BEGIN
                    CREATE TABLE dbo.CannedResponses (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Title       NVARCHAR(200)   NOT NULL,
                        Content     NVARCHAR(4000)  NOT NULL,
                        Category    NVARCHAR(100)   NULL,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        IsActive    BIT             NOT NULL DEFAULT 1
                    );

                    INSERT INTO dbo.CannedResponses (Title, Content, Category, SortOrder) VALUES
                    (N'Ticket Received',
                     N'Thank you for contacting IT Support. We have received your request and it is being reviewed by our team. We will update you shortly.',
                     N'General', 10),
                    (N'Requesting More Information',
                     N'To better assist you, could you please provide the following information:' + CHAR(13)+CHAR(10) +
                     N'- A description of the issue including any error messages' + CHAR(13)+CHAR(10) +
                     N'- The device name or asset tag affected' + CHAR(13)+CHAR(10) +
                     N'- When the issue first started' + CHAR(13)+CHAR(10) +
                     N'Thank you for your assistance.',
                     N'General', 20),
                    (N'Password Reset Instructions',
                     N'To reset your password please follow these steps:' + CHAR(13)+CHAR(10) +
                     N'1. Go to the login page and click Forgot Password' + CHAR(13)+CHAR(10) +
                     N'2. Enter your company email address' + CHAR(13)+CHAR(10) +
                     N'3. Check your email for the reset link (check spam if not received)' + CHAR(13)+CHAR(10) +
                     N'4. Follow the link to set a new password' + CHAR(13)+CHAR(10) +
                     N'Contact us if you need further assistance.',
                     N'Account', 30),
                    (N'Issue Resolved - Please Confirm',
                     N'We believe the issue described in this ticket has been resolved. Could you please confirm that everything is working correctly on your end? If the issue persists, please reply and we will continue to assist you.',
                     N'Resolution', 40),
                    (N'Remote Session Request',
                     N'To resolve this issue efficiently, I would like to connect to your device remotely. Please let me know a convenient time or if you are available now we can proceed immediately.',
                     N'Support', 50),
                    (N'Ticket Closed - No Response',
                     N'We have not received a response to our previous message. We will be closing this ticket as resolved. Please do not hesitate to open a new ticket if you continue to experience issues.',
                     N'Resolution', 60);
                END");

            // 11. Seed additional Company Branding AppSettings keys if not present
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyLogoUrl')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyLogoUrl', '', 'Branding', 'URL to your company logo (shown in the portal). Can be an external URL or a path like /images/company-logo.png');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyPhone')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyPhone', '', 'Branding', 'Company phone number displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyWebsite')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyWebsite', '', 'Branding', 'Company website URL displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyAddress')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyAddress', '', 'Branding', 'Company mailing address displayed in portal footer');");
        }
        catch (Exception ex)
        {
            // Log and continue — the app can still start even if upgrades fail
            // (tables may not exist yet on a fresh install)
            Console.WriteLine($"[DbInitializer] Schema upgrade warning: {ex.Message}");
        }
    }

    public static void Seed(ServiceDeskDbContext context)
    {
        // For SQL Server: tables are created via the SQL script (ServiceSphere_CreateTables.sql).
        // Only seed if the tables exist but are empty.
        try
        {
            if (context.Employees.Any())
            {
                // Ensure system roles and admin user exist even on subsequent runs
                SeedRolesAndAdminUser(context);
                return;
            }
        }
        catch
        {
            // Tables may not exist yet - skip seeding.
            // Run the Database/ServiceSphere_CreateTables.sql script on SQL Server first.
            return;
        }

        // Seed Employees
        var employees = new Employee[]
        {
            new() { FirstName = "John", LastName = "Smith", Email = "john.smith@probuild.com", Phone = "555-0101", Department = "IT", JobTitle = "IT Manager", HireDate = new DateTime(2020, 3, 15) },
            new() { FirstName = "Sarah", LastName = "Johnson", Email = "sarah.johnson@probuild.com", Phone = "555-0102", Department = "IT", JobTitle = "System Administrator", HireDate = new DateTime(2021, 6, 1) },
            new() { FirstName = "Mike", LastName = "Davis", Email = "mike.davis@probuild.com", Phone = "555-0103", Department = "IT", JobTitle = "Help Desk Technician", HireDate = new DateTime(2022, 1, 10) },
            new() { FirstName = "Emily", LastName = "Wilson", Email = "emily.wilson@probuild.com", Phone = "555-0104", Department = "Engineering", JobTitle = "Software Engineer", HireDate = new DateTime(2021, 9, 20) },
            new() { FirstName = "David", LastName = "Brown", Email = "david.brown@probuild.com", Phone = "555-0105", Department = "Finance", JobTitle = "Financial Analyst", HireDate = new DateTime(2020, 11, 5) },
            new() { FirstName = "Lisa", LastName = "Martinez", Email = "lisa.martinez@probuild.com", Phone = "555-0106", Department = "HR", JobTitle = "HR Specialist", HireDate = new DateTime(2022, 4, 15) },
            new() { FirstName = "Robert", LastName = "Taylor", Email = "robert.taylor@probuild.com", Phone = "555-0107", Department = "Operations", JobTitle = "Operations Lead", HireDate = new DateTime(2019, 8, 1) },
            new() { FirstName = "Jennifer", LastName = "Anderson", Email = "jennifer.anderson@probuild.com", Phone = "555-0108", Department = "Marketing", JobTitle = "Marketing Manager", HireDate = new DateTime(2021, 2, 14) },
        };
        context.Employees.AddRange(employees);
        context.SaveChanges();

        // Seed Company Services
        var services = new CompanyService[]
        {
            new() { Name = "Email & Collaboration", Description = "Microsoft 365 email, Teams, SharePoint services", Category = "Communication", Status = ServiceStatus.Active, ServiceOwner = "Sarah Johnson", SupportContact = "it-support@probuild.com", SlaHours = 4 },
            new() { Name = "VPN & Remote Access", Description = "Corporate VPN and remote desktop services", Category = "Network", Status = ServiceStatus.Active, ServiceOwner = "Sarah Johnson", SupportContact = "network@probuild.com", SlaHours = 2 },
            new() { Name = "ERP System", Description = "Enterprise resource planning system", Category = "Business Applications", Status = ServiceStatus.Active, ServiceOwner = "John Smith", SupportContact = "erp-support@probuild.com", SlaHours = 8 },
            new() { Name = "Print Services", Description = "Network printing and scanning services", Category = "Infrastructure", Status = ServiceStatus.Active, ServiceOwner = "Mike Davis", SupportContact = "helpdesk@probuild.com", SlaHours = 24 },
            new() { Name = "Backup & Recovery", Description = "Data backup and disaster recovery services", Category = "Infrastructure", Status = ServiceStatus.Active, ServiceOwner = "Sarah Johnson", SupportContact = "backup@probuild.com", SlaHours = 1 },
        };
        context.CompanyServices.AddRange(services);
        context.SaveChanges();

        // Seed Assets
        var assets = new Asset[]
        {
            new() { Name = "Dell Latitude 5540", AssetTag = "LAP-001", AssetType = AssetType.Laptop, Status = AssetStatus.Assigned, Manufacturer = "Dell", Model = "Latitude 5540", SerialNumber = "DL5540-001", PurchaseDate = new DateTime(2024, 1, 15), PurchaseCost = 1299.99m, WarrantyExpiry = new DateTime(2027, 1, 15), Location = "Office - 2nd Floor", AssignedToId = 4 },
            new() { Name = "Dell Latitude 5540", AssetTag = "LAP-002", AssetType = AssetType.Laptop, Status = AssetStatus.Assigned, Manufacturer = "Dell", Model = "Latitude 5540", SerialNumber = "DL5540-002", PurchaseDate = new DateTime(2024, 1, 15), PurchaseCost = 1299.99m, WarrantyExpiry = new DateTime(2027, 1, 15), Location = "Office - 3rd Floor", AssignedToId = 5 },
            new() { Name = "HP LaserJet Pro", AssetTag = "PRT-001", AssetType = AssetType.Printer, Status = AssetStatus.Available, Manufacturer = "HP", Model = "LaserJet Pro M404dn", SerialNumber = "HP-LJ-001", PurchaseDate = new DateTime(2023, 6, 1), PurchaseCost = 349.99m, WarrantyExpiry = new DateTime(2026, 6, 1), Location = "Office - 1st Floor" },
            new() { Name = "Dell UltraSharp 27\"", AssetTag = "MON-001", AssetType = AssetType.Monitor, Status = AssetStatus.Assigned, Manufacturer = "Dell", Model = "U2723QE", SerialNumber = "DU27-001", PurchaseDate = new DateTime(2024, 3, 10), PurchaseCost = 549.99m, WarrantyExpiry = new DateTime(2027, 3, 10), Location = "Office - 2nd Floor", AssignedToId = 4 },
            new() { Name = "Cisco Catalyst Switch", AssetTag = "NET-001", AssetType = AssetType.NetworkEquipment, Status = AssetStatus.Available, Manufacturer = "Cisco", Model = "Catalyst 9200L", SerialNumber = "CC-9200L-001", PurchaseDate = new DateTime(2023, 1, 20), PurchaseCost = 2499.99m, WarrantyExpiry = new DateTime(2028, 1, 20), Location = "Server Room" },
            new() { Name = "Dell PowerEdge R750", AssetTag = "SRV-001", AssetType = AssetType.Server, Status = AssetStatus.Available, Manufacturer = "Dell", Model = "PowerEdge R750", SerialNumber = "DPE-R750-001", PurchaseDate = new DateTime(2023, 9, 5), PurchaseCost = 8999.99m, WarrantyExpiry = new DateTime(2028, 9, 5), Location = "Server Room" },
            new() { Name = "iPhone 15 Pro", AssetTag = "PHN-001", AssetType = AssetType.Phone, Status = AssetStatus.Assigned, Manufacturer = "Apple", Model = "iPhone 15 Pro", SerialNumber = "APL-IP15P-001", PurchaseDate = new DateTime(2024, 10, 1), PurchaseCost = 999.99m, WarrantyExpiry = new DateTime(2026, 10, 1), Location = "Office", AssignedToId = 1 },
        };
        context.Assets.AddRange(assets);
        context.SaveChanges();

        // Seed Subscriptions
        var subscriptions = new Subscription[]
        {
            new() { Name = "Microsoft 365 Business Premium", Provider = "Microsoft", Description = "Email, Office apps, Teams, SharePoint, OneDrive", Status = SubscriptionStatus.Active, LicenseCount = 50, LicensesUsed = 42, MonthlyCost = 1100.00m, AnnualCost = 13200.00m, StartDate = new DateTime(2024, 1, 1), RenewalDate = new DateTime(2025, 1, 1) },
            new() { Name = "Adobe Creative Cloud", Provider = "Adobe", Description = "Photoshop, Illustrator, InDesign, Premiere Pro", Status = SubscriptionStatus.Active, LicenseCount = 10, LicensesUsed = 8, MonthlyCost = 549.90m, AnnualCost = 6598.80m, StartDate = new DateTime(2024, 3, 1), RenewalDate = new DateTime(2025, 3, 1) },
            new() { Name = "Zoom Business", Provider = "Zoom", Description = "Video conferencing and webinars", Status = SubscriptionStatus.Active, LicenseCount = 30, LicensesUsed = 28, MonthlyCost = 549.70m, AnnualCost = 6596.40m, StartDate = new DateTime(2024, 6, 1), RenewalDate = new DateTime(2025, 6, 1) },
            new() { Name = "Slack Business+", Provider = "Slack", Description = "Team messaging and collaboration", Status = SubscriptionStatus.Active, LicenseCount = 50, LicensesUsed = 45, MonthlyCost = 625.00m, AnnualCost = 7500.00m, StartDate = new DateTime(2024, 2, 1), RenewalDate = new DateTime(2025, 2, 1) },
            new() { Name = "Jira Software", Provider = "Atlassian", Description = "Project management and issue tracking", Status = SubscriptionStatus.Active, LicenseCount = 25, LicensesUsed = 20, MonthlyCost = 187.50m, AnnualCost = 2250.00m, StartDate = new DateTime(2024, 4, 1), RenewalDate = new DateTime(2025, 4, 1) },
        };
        context.Subscriptions.AddRange(subscriptions);
        context.SaveChanges();

        // Seed Roles + Admin Users
        SeedRolesAndAdminUser(context);

        // Seed Tickets
        var tickets = new Ticket[]
        {
            new() { Title = "Cannot connect to VPN", Description = "Unable to establish VPN connection from home. Getting timeout errors.", Category = TicketCategory.NetworkIssue, Status = TicketStatus.Open, Priority = TicketPriority.High, CreatedDate = DateTime.UtcNow.AddDays(-2), SubmittedById = 4, AssignedToId = 2, CompanyServiceId = 2 },
            new() { Title = "Laptop screen flickering", Description = "Dell laptop screen flickers intermittently, especially when on battery power.", Category = TicketCategory.HardwareIssue, Status = TicketStatus.InProgress, Priority = TicketPriority.Medium, CreatedDate = DateTime.UtcNow.AddDays(-5), SubmittedById = 5, AssignedToId = 3 },
            new() { Title = "New employee onboarding - IT setup", Description = "New hire starting next Monday in Marketing. Need full IT setup: laptop, email, Teams, required software.", Category = TicketCategory.EmployeeIssue, Status = TicketStatus.Open, Priority = TicketPriority.High, CreatedDate = DateTime.UtcNow.AddDays(-1), SubmittedById = 6, AssignedToId = 3 },
            new() { Title = "Email not syncing on mobile", Description = "Outlook app on iPhone stopped syncing emails two days ago.", Category = TicketCategory.SoftwareIssue, Status = TicketStatus.Resolved, Priority = TicketPriority.Low, CreatedDate = DateTime.UtcNow.AddDays(-7), UpdatedDate = DateTime.UtcNow.AddDays(-5), ResolvedDate = DateTime.UtcNow.AddDays(-5), SubmittedById = 8, AssignedToId = 3, CompanyServiceId = 1, ResolutionNotes = "Removed and re-added the email account. Syncing normally now." },
            new() { Title = "Request access to ERP system", Description = "Need access to the ERP system for financial reporting module.", Category = TicketCategory.ServiceRequest, Status = TicketStatus.Closed, Priority = TicketPriority.Medium, CreatedDate = DateTime.UtcNow.AddDays(-14), ResolvedDate = DateTime.UtcNow.AddDays(-12), ClosedDate = DateTime.UtcNow.AddDays(-10), SubmittedById = 5, AssignedToId = 1, CompanyServiceId = 3, ResolutionNotes = "ERP access granted with Finance Reader role." },
            new() { Title = "Printer jamming frequently", Description = "1st floor printer keeps jamming. Happens multiple times per day.", Category = TicketCategory.HardwareIssue, Status = TicketStatus.InProgress, Priority = TicketPriority.Medium, CreatedDate = DateTime.UtcNow.AddDays(-3), SubmittedById = 7, AssignedToId = 3, CompanyServiceId = 4 },
            new() { Title = "Suspected phishing email received", Description = "Received suspicious email claiming to be from CEO asking for wire transfer.", Category = TicketCategory.SecurityIncident, Status = TicketStatus.Open, Priority = TicketPriority.Critical, CreatedDate = DateTime.UtcNow, SubmittedById = 8, AssignedToId = 1 },
            new() { Title = "Software installation request - Visual Studio", Description = "Need Visual Studio 2024 Enterprise installed for development work.", Category = TicketCategory.ServiceRequest, Status = TicketStatus.Open, Priority = TicketPriority.Low, CreatedDate = DateTime.UtcNow.AddDays(-1), SubmittedById = 4, AssignedToId = 2 },
        };
        context.Tickets.AddRange(tickets);
        context.SaveChanges();
    }

    private static void SeedRolesAndAdminUser(ServiceDeskDbContext context)
    {
        // Seed system roles if not present
        var roleNames = new[]
        {
            ("Admin",    "Full system access — manage tickets, assets, settings, users.",
                         "ManageTickets,ManageAssets,ManageEmployees,ManageSubscriptions,ManageServices,ViewReports,ManageSettings,ManageUsers", true),
            ("IT Agent", "IT staff — manage tickets, assets, employees, subscriptions, services, view reports.",
                         "ManageTickets,ManageAssets,ManageEmployees,ManageSubscriptions,ManageServices,ViewReports", true),
            ("End User", "Non-IT employee — portal access only (submit and view own tickets).",
                         "Portal", true),
        };

        foreach (var (name, desc, perms, isSystem) in roleNames)
        {
            if (!context.Roles.Any(r => r.Name == name))
            {
                context.Roles.Add(new Role
                {
                    Name = name,
                    Description = desc,
                    Permissions = perms,
                    IsSystem = isSystem,
                    CreatedDate = DateTime.UtcNow
                });
            }
        }
        context.SaveChanges();

        // Seed admin portal user
        if (!context.PortalUsers.Any(u => u.Email == "admin@servicedeskpro.com"))
        {
            var adminRole = context.Roles.First(r => r.Name == "Admin");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "admin@servicedeskpro.com",
                FirstName = "System",
                LastName = "Admin",
                PasswordHash = PasswordService.HashPassword("Admin123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = adminRole.Id
            });
        }

        // Seed IT Agent portal user linked to John Smith (Id=1 from seed)
        if (!context.PortalUsers.Any(u => u.Email == "john.smith@probuild.com"))
        {
            var agentRole = context.Roles.First(r => r.Name == "IT Agent");
            var emp = context.Employees.FirstOrDefault(e => e.Email == "john.smith@probuild.com");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "john.smith@probuild.com",
                FirstName = "John",
                LastName = "Smith",
                PasswordHash = PasswordService.HashPassword("Agent123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = agentRole.Id,
                EmployeeId = emp?.Id
            });
        }

        // Seed End User portal user linked to Emily Wilson (non-IT)
        if (!context.PortalUsers.Any(u => u.Email == "emily.wilson@probuild.com"))
        {
            var userRole = context.Roles.First(r => r.Name == "End User");
            var emp = context.Employees.FirstOrDefault(e => e.Email == "emily.wilson@probuild.com");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "emily.wilson@probuild.com",
                FirstName = "Emily",
                LastName = "Wilson",
                PasswordHash = PasswordService.HashPassword("User123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = userRole.Id,
                EmployeeId = emp?.Id
            });
        }

        context.SaveChanges();
    }
}
