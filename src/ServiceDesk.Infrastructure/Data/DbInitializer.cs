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

            // 12. Create TicketSubCategories table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketSubCategories')
                BEGIN
                    CREATE TABLE dbo.TicketSubCategories (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Category    INT             NOT NULL,
                        Name        NVARCHAR(100)   NOT NULL,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        IsActive    BIT             NOT NULL DEFAULT 1
                    );

                    -- Seed default sub-categories
                    INSERT INTO dbo.TicketSubCategories (Category, Name, SortOrder) VALUES
                    (0, N'New Software Request', 10),   -- ServiceRequest
                    (0, N'Hardware Procurement', 20),
                    (0, N'Access / Permissions', 30),
                    (0, N'New User Onboarding', 40),
                    (1, N'Laptop / Desktop', 10),       -- HardwareIssue
                    (1, N'Printer / Scanner', 20),
                    (1, N'Monitor / Display', 30),
                    (1, N'Peripheral Devices', 40),
                    (2, N'Application Error', 10),      -- SoftwareIssue
                    (2, N'OS / Windows Issue', 20),
                    (2, N'Microsoft 365', 30),
                    (2, N'Antivirus / Security Tool', 40),
                    (3, N'Performance Issue', 10),      -- EmployeeIssue
                    (3, N'Login / Authentication', 20),
                    (3, N'Email Problem', 30),
                    (4, N'No Internet / Slow Connection', 10), -- NetworkIssue
                    (4, N'VPN / Remote Access', 20),
                    (4, N'Wi-Fi Issue', 30),
                    (4, N'Network Drive / Share', 40),
                    (5, N'Suspicious Email / Phishing', 10),   -- SecurityIncident
                    (5, N'Unauthorised Access', 20),
                    (5, N'Data Breach', 30),
                    (6, N'Other / General Inquiry', 10);        -- Other
                END");

            // 13. Add SubCategoryId to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'SubCategoryId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD SubCategoryId INT NULL
                        CONSTRAINT FK_Tickets_SubCategories
                        REFERENCES dbo.TicketSubCategories(Id)
                        ON DELETE SET NULL;
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
        try
        {
            // Only seeds system roles and the default admin login.
            // Sample data (employees, tickets, assets, etc.) is managed via the CSV import.
            SeedRolesAndAdminUser(context);
        }
        catch (Exception ex)
        {
            // Tables may not exist yet — skip seeding.
            // Run the Database/ServiceSphere_CreateTables.sql script on SQL Server first.
            Console.WriteLine($"[DbInitializer] Seed warning: {ex.Message}");
        }
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
