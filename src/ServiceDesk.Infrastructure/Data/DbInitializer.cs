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
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name            NVARCHAR(200)   NOT NULL,
                        Category        INT             NULL,
                        SubCategoryId   INT             NULL
                            CONSTRAINT FK_AssignmentRules_SubCategories
                            REFERENCES dbo.TicketSubCategories(Id)
                            ON DELETE SET NULL,
                        BranchId        INT             NULL
                            CONSTRAINT FK_AssignmentRules_Branches
                            REFERENCES dbo.Branches(Id)
                            ON DELETE SET NULL,
                        AssigneeId      INT             NOT NULL
                            CONSTRAINT FK_AssignmentRules_Employees
                            REFERENCES dbo.Employees(Id),
                        SortOrder       INT             NOT NULL DEFAULT 100,
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_AssignmentRules_Active_Sort
                        ON dbo.AssignmentRules (IsActive, SortOrder);
                END
                ELSE
                BEGIN
                    -- Upgrade: add SubCategoryId if table already exists
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.AssignmentRules') AND name = 'SubCategoryId'
                    )
                    BEGIN
                        ALTER TABLE dbo.AssignmentRules
                            ADD SubCategoryId INT NULL
                            CONSTRAINT FK_AssignmentRules_SubCategories
                            REFERENCES dbo.TicketSubCategories(Id)
                            ON DELETE SET NULL;
                    END
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

            // 14. Add DescriptionHtml column to Tickets (rich HTML from email)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'DescriptionHtml'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD DescriptionHtml NVARCHAR(MAX) NULL;
                END");

            // 14b. Add UserGroupId column to Tickets (group assignment)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'UserGroupId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD UserGroupId INT NULL
                        CONSTRAINT FK_Tickets_UserGroups
                        REFERENCES dbo.UserGroups(Id)
                        ON DELETE SET NULL;
                END");

            // 15. Create SavedTicketViews table (user filter presets)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SavedTicketViews')
                BEGIN
                    CREATE TABLE dbo.SavedTicketViews (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name                NVARCHAR(100)   NOT NULL,
                        OwnerPortalUserId   INT             NULL
                            CONSTRAINT FK_SavedTicketViews_PortalUsers
                            REFERENCES dbo.PortalUsers(Id)
                            ON DELETE CASCADE,
                        IsDefault           BIT             NOT NULL DEFAULT 0,
                        IsShared            BIT             NOT NULL DEFAULT 0,
                        FilterStatuses      NVARCHAR(200)   NULL,
                        FilterStatus        INT             NULL,
                        FilterCategory      INT             NULL,
                        FilterPriority      INT             NULL,
                        FilterBranchId      INT             NULL
                            CONSTRAINT FK_SavedTicketViews_Branches
                            REFERENCES dbo.Branches(Id)
                            ON DELETE SET NULL,
                        FilterDepartment    NVARCHAR(100)   NULL,
                        FilterGroupId       INT             NULL
                            CONSTRAINT FK_SavedTicketViews_UserGroups
                            REFERENCES dbo.UserGroups(Id)
                            ON DELETE SET NULL,
                        FilterAssignedToMe  BIT             NOT NULL DEFAULT 0,
                        FilterUnassignedOnly BIT            NOT NULL DEFAULT 0,
                        FilterUnmatchedOnly BIT             NOT NULL DEFAULT 0,
                        SortBy              NVARCHAR(20)    NOT NULL DEFAULT 'id',
                        SortDir             NVARCHAR(4)     NOT NULL DEFAULT 'desc',
                        PageSize            INT             NOT NULL DEFAULT 25,
                        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_SavedTicketViews_Owner_Shared
                        ON dbo.SavedTicketViews (OwnerPortalUserId, IsShared);
                END
                ELSE
                BEGIN
                    -- Upgrade: add FilterStatuses column if table already exists
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterStatuses'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterStatuses NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterCategories'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterCategories NVARCHAR(300) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterPriorities'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterPriorities NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterBranchIds'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterBranchIds NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterDepartments'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterDepartments NVARCHAR(500) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.TicketNotes') AND name = 'ContentHtml'
                    )
                    BEGIN
                        ALTER TABLE dbo.TicketNotes ADD ContentHtml NVARCHAR(MAX) NULL;
                    END
                END");

            // Seed the system-level default view (active/non-resolved tickets).
            // OwnerPortalUserId = NULL means system-owned; applies to every user
            // who has not set their own personal default.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.SavedTicketViews
                    WHERE OwnerPortalUserId IS NULL AND IsDefault = 1
                )
                BEGIN
                    INSERT INTO dbo.SavedTicketViews
                        (Name, OwnerPortalUserId, IsDefault, IsShared,
                         FilterStatuses, SortBy, SortDir, PageSize, CreatedDate)
                    VALUES
                        (N'Active Tickets (Default)', NULL, 1, 1,
                         N'Open,InProgress,OnHold', 'id', 'desc', 25, SYSUTCDATETIME());
                END");

            // 16b. Add Extension column to Employees
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'Extension'
                )
                BEGIN
                    ALTER TABLE dbo.Employees ADD Extension NVARCHAR(10) NULL;
                END");

            // 17. Create CategoryKeywords table (DB-backed keyword detection for auto-categorisation)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CategoryKeywords')
                BEGIN
                    CREATE TABLE dbo.CategoryKeywords (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Category    INT             NOT NULL,
                        Keyword     NVARCHAR(100)   NOT NULL,
                        IsActive    BIT             NOT NULL DEFAULT 1,
                        CreatedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_CategoryKeywords_Category_Active
                        ON dbo.CategoryKeywords (Category, IsActive);

                    -- Seed default keywords (mirrors AssignmentResolverService hardcoded dictionary)
                    -- HardwareIssue = 1
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (1, N'printer'), (1, N'printing'), (1, N'keyboard'), (1, N'mouse'),
                    (1, N'monitor'), (1, N'screen'), (1, N'display'), (1, N'laptop'),
                    (1, N'desktop'), (1, N'computer'), (1, N'pc'), (1, N'hardware'),
                    (1, N'device'), (1, N'battery'), (1, N'charger'), (1, N'dock'),
                    (1, N'docking'), (1, N'headset'), (1, N'webcam'), (1, N'scanner'),
                    (1, N'projector'), (1, N'broken'), (1, N'damaged'), (1, N'physical'),
                    (1, N'power'), (1, N'overheating'), (1, N'fan noise');

                    -- SoftwareIssue = 2
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (2, N'software'), (2, N'application'), (2, N'app'), (2, N'program'),
                    (2, N'install'), (2, N'installation'), (2, N'uninstall'), (2, N'update'),
                    (2, N'upgrade'), (2, N'crash'), (2, N'crashes'), (2, N'error'),
                    (2, N'errors'), (2, N'bug'), (2, N'license'), (2, N'activation'),
                    (2, N'office'), (2, N'word'), (2, N'excel'), (2, N'outlook'),
                    (2, N'teams'), (2, N'zoom'), (2, N'adobe'), (2, N'browser'),
                    (2, N'chrome'), (2, N'firefox'), (2, N'edge'), (2, N'slow'),
                    (2, N'freezing'), (2, N'frozen'), (2, N'not responding'),
                    (2, N'blue screen'), (2, N'bsod'), (2, N'driver'),
                    (2, N'operating system'), (2, N'windows'), (2, N'macos'), (2, N'patch');

                    -- NetworkIssue = 4
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (4, N'vpn'), (4, N'network'), (4, N'internet'), (4, N'wifi'),
                    (4, N'wi-fi'), (4, N'wireless'), (4, N'ethernet'), (4, N'connection'),
                    (4, N'connectivity'), (4, N'firewall'), (4, N'dns'), (4, N'dhcp'),
                    (4, N'ip address'), (4, N'bandwidth'), (4, N'slow internet'),
                    (4, N'no internet'), (4, N'network drive'), (4, N'mapped drive'),
                    (4, N'remote access'), (4, N'remote desktop'), (4, N'rdp'),
                    (4, N'switch'), (4, N'router'), (4, N'cable');

                    -- SecurityIncident = 5
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (5, N'security'), (5, N'phishing'), (5, N'phish'), (5, N'suspicious'),
                    (5, N'hack'), (5, N'hacked'), (5, N'virus'), (5, N'malware'),
                    (5, N'ransomware'), (5, N'spyware'), (5, N'trojan'), (5, N'spam'),
                    (5, N'unauthorized'), (5, N'breach'), (5, N'password reset'),
                    (5, N'account locked'), (5, N'compromised'), (5, N'scam'),
                    (5, N'fraud'), (5, N'social engineering'), (5, N'2fa'), (5, N'mfa');

                    -- EmployeeIssue = 3
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (3, N'onboarding'), (3, N'new employee'), (3, N'new hire'),
                    (3, N'offboarding'), (3, N'termination'), (3, N'terminated'),
                    (3, N'access request'), (3, N'new user'), (3, N'user setup'),
                    (3, N'account setup'), (3, N'leave'), (3, N'absence'),
                    (3, N'transfer'), (3, N'promotion'), (3, N'department change'),
                    (3, N'badge'), (3, N'id card'), (3, N'equipment request'),
                    (3, N'role change');

                    -- ServiceRequest = 0
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (0, N'request'), (0, N'order'), (0, N'setup'), (0, N'configure'),
                    (0, N'configuration'), (0, N'provision'), (0, N'provisioning'),
                    (0, N'access'), (0, N'permission'), (0, N'grant'),
                    (0, N'create account'), (0, N'new account'), (0, N'service'),
                    (0, N'question'), (0, N'help'), (0, N'how to'), (0, N'assistance');
                END");

            // 18. Seed additional Company Branding AppSettings keys if not present
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
                    VALUES ('CompanyAddress', '', 'Branding', 'Company mailing address displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'BrandColor')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('BrandColor', '#4f46e5', 'Branding', 'Primary brand colour used in email headers and PDF exports (hex format, e.g. #4f46e5)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailHeaderTagline')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailHeaderTagline', 'IT Service Desk', 'Branding', 'Tagline shown below the company name in the email header banner (e.g. ''IT Service Desk'', ''Support Team'')');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailShowLogo')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailShowLogo', 'true', 'Branding', 'Show the company logo image in outgoing email headers (requires Company Logo URL to be set)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailFooterText')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailFooterText', '', 'Branding', 'Custom footer text for all outgoing emails. Leave blank to use the default automated-notification message.');");

            // 19. Create EmailTemplates table (DB-backed email template management)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmailTemplates')
                BEGIN
                    CREATE TABLE dbo.EmailTemplates (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        [Key]           NVARCHAR(50)    NOT NULL,
                        Name            NVARCHAR(100)   NOT NULL,
                        Description     NVARCHAR(500)   NULL,
                        SubjectTemplate NVARCHAR(300)   NULL,
                        BodyTemplate    NVARCHAR(MAX)   NULL,
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        UpdatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE UNIQUE INDEX IX_EmailTemplates_Key ON dbo.EmailTemplates ([Key]);

                    -- Seed default template metadata (BodyTemplate left NULL — system defaults used until admin customises)
                    INSERT INTO dbo.EmailTemplates ([Key], Name, Description, SubjectTemplate) VALUES
                    (N'TicketCreated',
                     N'Ticket Created Confirmation',
                     N'Sent to the submitter when a new ticket is created. Confirms receipt and provides the ticket reference.',
                     N'[#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'TicketAssigned',
                     N'Ticket Assigned — Agent Notification',
                     N'Sent to the IT agent when a ticket is assigned to them.',
                     N'Assigned: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'TicketUpdated',
                     N'Ticket Status Update',
                     N'Sent to the submitter when the ticket status or priority changes.',
                     N'Updated: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'NoteAdded',
                     N'New Comment / Note',
                     N'Sent to the submitter when an IT agent adds a public note or comment to the ticket.',
                     N'Re: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'PasswordReset',
                     N'Password Reset Request',
                     N'Sent to a portal user when they request a password reset link.',
                     N'Reset Your {{CompanyName}} Password');
                END");
            // 20. Create AiRecommendations table (AI triage suggestions per ticket)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AiRecommendations')
                BEGIN
                    CREATE TABLE dbo.AiRecommendations (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId            INT             NOT NULL
                            CONSTRAINT FK_AiRecommendations_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        SuggestedCategory   INT             NULL,
                        SuggestedPriority   INT             NULL,
                        SuggestedAssigneeId INT             NULL
                            CONSTRAINT FK_AiRecommendations_Employees
                            REFERENCES dbo.Employees(Id)
                            ON DELETE SET NULL,
                        CategoryConfidence  REAL            NOT NULL DEFAULT 0,
                        PriorityConfidence  REAL            NOT NULL DEFAULT 0,
                        AiSummary           NVARCHAR(2000)  NULL,
                        AiDraftReply        NVARCHAR(MAX)   NULL,
                        Status              NVARCHAR(20)    NOT NULL DEFAULT 'Pending',
                        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        ReviewedDate        DATETIME2       NULL,
                        ReviewedBy          NVARCHAR(200)   NULL
                    );

                    CREATE INDEX IX_AiRecommendations_Ticket_Status
                        ON dbo.AiRecommendations (TicketId, Status);
                END");

            // 21. Create AiRunLogs table (audit log for ML.NET training / prediction runs)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AiRunLogs')
                BEGIN
                    CREATE TABLE dbo.AiRunLogs (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        RunDate             DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        RunType             NVARCHAR(20)    NOT NULL DEFAULT 'Training',
                        TrainingTicketCount INT             NOT NULL DEFAULT 0,
                        ModelVersion        NVARCHAR(50)    NOT NULL DEFAULT '',
                        Success             BIT             NOT NULL DEFAULT 1,
                        ErrorMessage        NVARCHAR(1000)  NULL,
                        DurationMs          FLOAT           NOT NULL DEFAULT 0
                    );
                END");

            // 22. Seed AI feature-flag AppSettings keys
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiTriageEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiTriageEnabled', 'false', 'AI Triage',
                            'Enable ML.NET-powered AI triage suggestions for new tickets');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiTriageMode')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiTriageMode', 'RecommendOnly', 'AI Triage',
                            'RecommendOnly: show suggestions to agents | AutoApply: automatically apply suggestions when confidence is high');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiConfidenceThreshold')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiConfidenceThreshold', '0.65', 'AI Triage',
                            'Minimum confidence score (0.0–1.0) required before a triage suggestion is shown or applied');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiMinTrainingTickets')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiMinTrainingTickets', '20', 'AI Triage',
                            'Minimum number of resolved/closed tickets required before the AI model is trained');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaEnabled', 'false', 'AI Triage',
                            'Enable Ollama local LLM for AI-generated ticket summaries and draft replies');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaUrl')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaUrl', 'http://localhost:11434', 'AI Triage',
                            'URL of the locally running Ollama server (default: http://localhost:11434)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaModel')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaModel', 'phi3', 'AI Triage',
                            'Ollama model name to use for text generation (e.g. phi3, llama3.1, mistral)');");

            // 23. Add escalation columns to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'IsEscalated'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD IsEscalated       BIT           NOT NULL DEFAULT 0;
                    ALTER TABLE dbo.Tickets ADD EscalationReason  NVARCHAR(500) NULL;
                    ALTER TABLE dbo.Tickets ADD EscalatedAt       DATETIME2     NULL;
                    ALTER TABLE dbo.Tickets ADD EscalatedById     INT           NULL
                        CONSTRAINT FK_Tickets_EscalatedBy
                        REFERENCES dbo.Employees(Id)
                        ON DELETE SET NULL;
                END");

            // 24. Add ResolutionType column to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'ResolutionType'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD ResolutionType NVARCHAR(100) NULL;
                END");

            // 25. Seed notification-trigger AppSettings keys
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnTicketCreated')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnTicketCreated', 'true', 'Notifications',
                            'Send confirmation email to the requester when a new ticket is created');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnStatusChange')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnStatusChange', 'true', 'Notifications',
                            'Send email to the requester when the ticket status changes');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnAssignment')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnAssignment', 'true', 'Notifications',
                            'Send email to the assigned agent when a ticket is assigned to them');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnNoteAdded')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnNoteAdded', 'true', 'Notifications',
                            'Send email to the requester when a public comment is added to their ticket');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnEscalation')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnEscalation', 'true', 'Notifications',
                            'Send email to the assigned agent and admin when a ticket is escalated');"  );

            // 26. Seed Report Request category sub-categories and keywords (Category = 7)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketSubCategories WHERE Category = 7)
                BEGIN
                    INSERT INTO dbo.TicketSubCategories (Category, Name, SortOrder) VALUES
                    (7, N'AR Report',                10),
                    (7, N'Installation Report',      20),
                    (7, N'Inventory Report',         30),
                    (7, N'Rebate Report',            40),
                    (7, N'Sales Report',             50),
                    (7, N'Management Report',        60),
                    (7, N'Custom / Ad-Hoc Report',   70),
                    (7, N'General Report Request',   80);
                END

                -- Seed auto-classification keywords for Report Request
                IF NOT EXISTS (SELECT 1 FROM dbo.CategoryKeywords WHERE Category = 7)
                BEGIN
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (7, N'report'),
                    (7, N'reporting'),
                    (7, N'ar report'),
                    (7, N'accounts receivable report'),
                    (7, N'installation report'),
                    (7, N'inventory report'),
                    (7, N'rebate report'),
                    (7, N'sales report'),
                    (7, N'management report'),
                    (7, N'generate report'),
                    (7, N'run report'),
                    (7, N'pull report'),
                    (7, N'export report'),
                    (7, N'report request'),
                    (7, N'monthly report'),
                    (7, N'weekly report'),
                    (7, N'quarterly report');
                END");

            // 27. Create TicketCategories table and seed system categories
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketCategories')
                BEGIN
                    CREATE TABLE dbo.TicketCategories (
                        Id          INT             NOT NULL PRIMARY KEY,
                        Name        NVARCHAR(100)   NOT NULL,
                        IsSystem    BIT             NOT NULL DEFAULT 1,
                        IsActive    BIT             NOT NULL DEFAULT 1,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        Icon        NVARCHAR(60)    NULL,
                        Color       NVARCHAR(20)    NULL
                    );
                END

                -- Seed system categories (IDs 0-7 match the original TicketCategory enum values)
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 0)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (0, N'Service Request',   1, 1, 10, N'bi-clipboard-check',    N'#4f46e5');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 1)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (1, N'Hardware Issue',    1, 1, 20, N'bi-pc-display',         N'#0d6efd');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 2)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (2, N'Software Issue',    1, 1, 30, N'bi-code-square',        N'#198754');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 3)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (3, N'Employee Issue',    1, 1, 40, N'bi-person-badge',       N'#fd7e14');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 4)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (4, N'Network Issue',     1, 1, 50, N'bi-router',             N'#0dcaf0');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 5)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (5, N'Security Incident', 1, 1, 60, N'bi-shield-exclamation', N'#dc3545');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 6)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (6, N'Other',             1, 1, 70, N'bi-question-circle',    N'#6c757d');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 7)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (7, N'Report Request',    1, 1, 80, N'bi-file-earmark-bar-graph', N'#20c997');

                -- 28. Seed SLA policy hours AppSettings
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursCritical')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursCritical', '4', 'SLA', 'SLA response time in hours for Critical priority tickets (default: 4)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursHigh')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursHigh', '8', 'SLA', 'SLA response time in hours for High priority tickets (default: 8)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursMedium')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursMedium', '24', 'SLA', 'SLA response time in hours for Medium priority tickets (default: 24)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursLow')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursLow', '72', 'SLA', 'SLA response time in hours for Low priority tickets (default: 72)');

                -- Migrate SavedTicketViews.FilterCategories from enum names to numeric IDs
                -- Only runs when alphabetic names are still present (one-time migration)
                IF EXISTS (SELECT 1 FROM dbo.SavedTicketViews WHERE FilterCategories LIKE '%[a-zA-Z]%')
                BEGIN
                    UPDATE dbo.SavedTicketViews
                    SET FilterCategories =
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            ISNULL(FilterCategories, ''),
                            'ServiceRequest',   '0'),
                            'HardwareIssue',    '1'),
                            'SoftwareIssue',    '2'),
                            'EmployeeIssue',    '3'),
                            'NetworkIssue',     '4'),
                            'SecurityIncident', '5'),
                            'ReportRequest',    '7'),
                            'Other',            '6')
                    WHERE FilterCategories IS NOT NULL AND FilterCategories LIKE '%[a-zA-Z]%';
                END");
            // 29. Seed Portal Branding AppSettings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalWelcomeMessage')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalWelcomeMessage', 'Track and manage your IT support requests.', 'Portal Branding',
                            'Short tagline shown below the greeting on the portal dashboard');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalSupportTitle')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalSupportTitle', 'IT Support Portal', 'Portal Branding',
                            'Text appended to the company name in portal browser tab titles');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalAnnouncement')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalAnnouncement', '', 'Portal Branding',
                            'Optional announcement banner shown at the top of every portal page. Leave blank to hide.');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalAnnouncementType')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalAnnouncementType', 'info', 'Portal Branding',
                            'Bootstrap alert colour for the announcement banner: info, warning, danger, or success');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalShowKnowledgeBase')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalShowKnowledgeBase', 'true', 'Portal Branding',
                            'Show the Knowledge Base link in the portal navigation bar');");

            // 30. ITAM enhancements — new asset-related tables + column additions
            context.Database.ExecuteSqlRaw(@"
                -- Network / system fields on Assets
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'IpAddress')
                    ALTER TABLE dbo.Assets ADD IpAddress NVARCHAR(50) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'MacAddress')
                    ALTER TABLE dbo.Assets ADD MacAddress NVARCHAR(17) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'Hostname')
                    ALTER TABLE dbo.Assets ADD Hostname NVARCHAR(200) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'OsVersion')
                    ALTER TABLE dbo.Assets ADD OsVersion NVARCHAR(100) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'OsBuild')
                    ALTER TABLE dbo.Assets ADD OsBuild NVARCHAR(50) NULL;");

            context.Database.ExecuteSqlRaw(@"
                -- AssetId FK on Tickets (optional link to related asset)
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'AssetId')
                BEGIN
                    ALTER TABLE dbo.Tickets ADD AssetId INT NULL;
                    ALTER TABLE dbo.Tickets ADD CONSTRAINT FK_Tickets_Assets
                        FOREIGN KEY (AssetId) REFERENCES dbo.Assets(Id) ON DELETE SET NULL;
                END");

            context.Database.ExecuteSqlRaw(@"
                -- Assignment history / custody chain
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAssignmentHistory')
                    CREATE TABLE dbo.AssetAssignmentHistory (
                        Id              INT IDENTITY PRIMARY KEY,
                        AssetId         INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        AssignedToId    INT NULL REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
                        AssignedById    INT NULL REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
                        AssignedDate    DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        ReturnedDate    DATETIME2 NULL,
                        Notes           NVARCHAR(500) NULL
                    );

                -- Asset change audit log
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAuditLogs')
                    CREATE TABLE dbo.AssetAuditLogs (
                        Id              INT IDENTITY PRIMARY KEY,
                        AssetId         INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        FieldName       NVARCHAR(100) NOT NULL,
                        OldValue        NVARCHAR(1000) NULL,
                        NewValue        NVARCHAR(1000) NULL,
                        ChangedDate     DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        ChangedByEmail  NVARCHAR(200) NOT NULL
                    );

                -- Password / credential vault
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetCredentials')
                    CREATE TABLE dbo.AssetCredentials (
                        Id                  INT IDENTITY PRIMARY KEY,
                        AssetId             INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        Label               NVARCHAR(100) NOT NULL,
                        Username            NVARCHAR(200) NULL,
                        EncryptedPassword   NVARCHAR(MAX) NOT NULL,
                        Url                 NVARCHAR(500) NULL,
                        Notes               NVARCHAR(500) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2 NULL,
                        CreatedByEmail      NVARCHAR(200) NOT NULL
                    );

                -- Asset file attachments
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAttachments')
                    CREATE TABLE dbo.AssetAttachments (
                        Id                  INT IDENTITY PRIMARY KEY,
                        AssetId             INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        FileName            NVARCHAR(260) NOT NULL,
                        StoredFileName      NVARCHAR(260) NOT NULL,
                        FileSizeBytes       BIGINT NOT NULL DEFAULT 0,
                        ContentType         NVARCHAR(100) NOT NULL DEFAULT 'application/octet-stream',
                        UploadedDate        DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UploadedByEmail     NVARCHAR(200) NOT NULL
                    );

                -- CMDB-lite asset-to-asset relationship web
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetRelationships')
                    CREATE TABLE dbo.AssetRelationships (
                        Id                  INT IDENTITY PRIMARY KEY,
                        SourceAssetId       INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        TargetAssetId       INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE NO ACTION,
                        RelationshipType    NVARCHAR(80) NOT NULL DEFAULT 'ConnectedTo',
                        Notes               NVARCHAR(300) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        CreatedByEmail      NVARCHAR(200) NULL
                    );");

            // 31. Employee credential vault + Subscription → Asset link
            context.Database.ExecuteSqlRaw(@"
                -- Per-employee IT-managed credential vault
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeCredentials')
                    CREATE TABLE dbo.EmployeeCredentials (
                        Id                  INT IDENTITY PRIMARY KEY,
                        EmployeeId          INT NOT NULL REFERENCES dbo.Employees(Id) ON DELETE CASCADE,
                        Label               NVARCHAR(100) NOT NULL,
                        Username            NVARCHAR(200) NULL,
                        EncryptedPassword   NVARCHAR(MAX) NOT NULL,
                        Url                 NVARCHAR(500) NULL,
                        Notes               NVARCHAR(500) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2 NULL,
                        CreatedByEmail      NVARCHAR(200) NOT NULL
                    );

                -- Link software subscriptions to a specific asset (device-based license tracking)
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Subscriptions') AND name = 'AssetId')
                BEGIN
                    ALTER TABLE dbo.Subscriptions ADD AssetId INT NULL;
                    ALTER TABLE dbo.Subscriptions ADD CONSTRAINT FK_Subscriptions_Assets
                        FOREIGN KEY (AssetId) REFERENCES dbo.Assets(Id) ON DELETE SET NULL;
                END");

            // 32. Google Workspace integration settings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GoogleWorkspaceSettings')
                    CREATE TABLE dbo.GoogleWorkspaceSettings (
                        Id                          INT IDENTITY PRIMARY KEY,
                        EncryptedServiceAccountJson NVARCHAR(MAX) NULL,
                        AdminEmail                  NVARCHAR(300) NOT NULL DEFAULT '',
                        Domain                      NVARCHAR(200) NOT NULL DEFAULT '',
                        IsConfigured                BIT NOT NULL DEFAULT 0,
                        LastTestedDate              DATETIME2 NULL,
                        LastTestResult              NVARCHAR(500) NULL,
                        LastTestPassed              BIT NOT NULL DEFAULT 0,
                        SignatureTemplate           NVARCHAR(MAX) NULL,
                        CreatedDate                 DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate                 DATETIME2 NULL
                    );");

            // 33. CSAT survey table + feature-flag AppSettings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CsatSurveys')
                BEGIN
                    CREATE TABLE dbo.CsatSurveys (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId        INT             NOT NULL
                            CONSTRAINT FK_CsatSurveys_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        Token           NVARCHAR(64)    NOT NULL,
                        Score           INT             NULL,
                        Feedback        NVARCHAR(1000)  NULL,
                        SentDate        DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        CompletedDate   DATETIME2       NULL,
                        CONSTRAINT UQ_CsatSurveys_Token UNIQUE (Token)
                    );

                    CREATE INDEX IX_CsatSurveys_TicketId
                        ON dbo.CsatSurveys (TicketId);
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CsatSurveyEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CsatSurveyEnabled', 'false', 'Surveys',
                            'Send a post-resolution satisfaction survey (1–5 stars) to the ticket requester when a ticket is closed');");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'LastGoogleSignatureSync')
                    ALTER TABLE dbo.Employees ADD LastGoogleSignatureSync DATETIME2 NULL;");
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
            ("Viewer",   "Read-only access to the web app (dashboard, tickets, assets, subscriptions, services, reports). Can also use the portal for own tickets.",
                         "ViewReports,ViewDashboard,ViewTickets,ViewAssets,ViewSubscriptions,ViewServices", true),
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
