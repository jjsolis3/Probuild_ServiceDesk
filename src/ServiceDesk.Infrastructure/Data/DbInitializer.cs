using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;

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
                return; // Already seeded
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
}
