-- =====================================================================
-- ServiceSphere Database - Email Upgrade Migration Script
-- Target: Microsoft SQL Server 2016+
-- Database: ServiceSphere
-- =====================================================================
--
-- PURPOSE:
--   This migration upgrades the ServiceSphere email subsystem to use the
--   Gmail API instead of IMAP/SMTP credentials. It also introduces two
--   new tables to support ticket threading (TicketNotes) and email
--   header tracking / de-duplication (TicketEmails).
--
-- CHANGES:
--   1. EmailConfigurations table:
--      - DROP legacy IMAP columns (ImapServer, ImapPort, Username,
--        Password, DefaultTicketCategoryId)
--      - ADD Gmail API columns (GmailClientId, GmailClientSecret,
--        GmailRefreshToken, GmailAccessToken, GmailTokenExpiry,
--        GmailHistoryId, AutoReplyOnNewTicket, IsAuthorized)
--
--   2. New table: TicketNotes
--      - Stores threaded comments/notes against tickets
--      - Supports portal, email, and internal note sources
--
--   3. New table: TicketEmails
--      - Tracks Gmail message IDs and RFC 2822 headers for threading
--      - UNIQUE constraint on GmailMessageId prevents duplicate processing
--
--   4. Seed data update for existing EmailConfigurations row
--
-- IDEMPOTENCY:
--   This script is safe to run multiple times. All DDL operations are
--   guarded with IF EXISTS / IF NOT EXISTS checks.
--
-- =====================================================================

USE [ServiceSphere];
GO

-- =====================================================================
-- STEP 1: Modify EmailConfigurations - DROP legacy IMAP columns
-- =====================================================================

-- Drop ImapServer
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'ImapServer'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations DROP COLUMN ImapServer;
END;
GO

-- Drop ImapPort
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'ImapPort'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations DROP COLUMN ImapPort;
END;
GO

-- Drop Username
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'Username'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations DROP COLUMN Username;
END;
GO

-- Drop Password
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'Password'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations DROP COLUMN Password;
END;
GO

-- Drop DefaultTicketCategoryId
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'DefaultTicketCategoryId'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations DROP COLUMN DefaultTicketCategoryId;
END;
GO

-- =====================================================================
-- STEP 2: Modify EmailConfigurations - ADD Gmail API columns
-- =====================================================================

-- Add GmailClientId
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailClientId'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailClientId NVARCHAR(500) NULL;
END;
GO

-- Add GmailClientSecret
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailClientSecret'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailClientSecret NVARCHAR(500) NULL;
END;
GO

-- Add GmailRefreshToken
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailRefreshToken'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailRefreshToken NVARCHAR(MAX) NULL;
END;
GO

-- Add GmailAccessToken
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailAccessToken'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailAccessToken NVARCHAR(MAX) NULL;
END;
GO

-- Add GmailTokenExpiry
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailTokenExpiry'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailTokenExpiry DATETIME2 NULL;
END;
GO

-- Add GmailHistoryId
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'GmailHistoryId'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD GmailHistoryId NVARCHAR(100) NULL;
END;
GO

-- Add AutoReplyOnNewTicket
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'AutoReplyOnNewTicket'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD AutoReplyOnNewTicket BIT NOT NULL DEFAULT 1;
END;
GO

-- Add IsAuthorized
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name = 'IsAuthorized'
)
BEGIN
    ALTER TABLE dbo.EmailConfigurations
        ADD IsAuthorized BIT NOT NULL DEFAULT 0;
END;
GO

-- =====================================================================
-- STEP 3: Create TicketNotes table
-- =====================================================================
IF OBJECT_ID('dbo.TicketNotes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TicketNotes (
        Id              INT             IDENTITY(1,1) NOT NULL,
        TicketId        INT             NOT NULL,
        AuthorName      NVARCHAR(200)   NOT NULL,
        AuthorEmail     NVARCHAR(200)   NULL,
        Content         NVARCHAR(MAX)   NOT NULL,
        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        Source          NVARCHAR(20)    NOT NULL DEFAULT 'Portal',
        IsInternal      BIT             NOT NULL DEFAULT 0,

        CONSTRAINT PK_TicketNotes PRIMARY KEY (Id),
        CONSTRAINT FK_TicketNotes_Tickets FOREIGN KEY (TicketId)
            REFERENCES dbo.Tickets(Id) ON DELETE CASCADE
    );

    -- Index on TicketId for fast lookups by ticket
    CREATE INDEX IX_TicketNotes_TicketId ON dbo.TicketNotes (TicketId);
END;
GO

-- =====================================================================
-- STEP 4: Create TicketEmails table
-- =====================================================================
IF OBJECT_ID('dbo.TicketEmails', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TicketEmails (
        Id              INT             IDENTITY(1,1) NOT NULL,
        TicketId        INT             NOT NULL,
        GmailMessageId  NVARCHAR(500)   NOT NULL,
        MessageId       NVARCHAR(500)   NULL,
        InReplyTo       NVARCHAR(500)   NULL,
        [References]    NVARCHAR(MAX)   NULL,
        FromAddress     NVARCHAR(200)   NOT NULL,
        FromName        NVARCHAR(200)   NULL,
        Subject         NVARCHAR(500)   NULL,
        Direction       NVARCHAR(10)    NOT NULL DEFAULT 'Inbound',
        ProcessedDate   DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT PK_TicketEmails PRIMARY KEY (Id),
        CONSTRAINT FK_TicketEmails_Tickets FOREIGN KEY (TicketId)
            REFERENCES dbo.Tickets(Id) ON DELETE CASCADE
    );

    -- UNIQUE index on GmailMessageId to prevent duplicate email processing
    CREATE UNIQUE INDEX IX_TicketEmails_GmailMessageId
        ON dbo.TicketEmails (GmailMessageId);

    -- Index on MessageId for RFC 2822 threading lookups
    CREATE INDEX IX_TicketEmails_MessageId
        ON dbo.TicketEmails (MessageId);

    -- Index on TicketId for fast lookups by ticket
    CREATE INDEX IX_TicketEmails_TicketId
        ON dbo.TicketEmails (TicketId);
END;
GO

-- =====================================================================
-- STEP 5: Update existing EmailConfigurations seed data
-- =====================================================================
-- Set reasonable defaults for the existing configuration row (if any).
-- This ensures the record is in a clean state after the schema change.
IF EXISTS (SELECT 1 FROM dbo.EmailConfigurations WHERE Id = 1)
BEGIN
    UPDATE dbo.EmailConfigurations
    SET IsAuthorized = 0,
        AutoReplyOnNewTicket = 1
    WHERE Id = 1;
END;
GO

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '=============================================';
PRINT 'Email Upgrade Migration - Verification';
PRINT '=============================================';

-- Confirm new columns exist on EmailConfigurations
SELECT c.name AS ColumnName, t.name AS DataType, c.max_length, c.is_nullable
FROM sys.columns c
INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.EmailConfigurations')
ORDER BY c.column_id;

-- Confirm legacy columns are gone
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
      AND name IN ('ImapServer', 'ImapPort', 'Username', 'Password', 'DefaultTicketCategoryId')
)
    PRINT 'OK: Legacy IMAP columns have been removed.';
ELSE
    PRINT 'WARNING: Some legacy IMAP columns still exist.';

-- Confirm new tables
IF OBJECT_ID('dbo.TicketNotes', 'U') IS NOT NULL
    PRINT 'OK: TicketNotes table exists.';
ELSE
    PRINT 'ERROR: TicketNotes table was NOT created.';

IF OBJECT_ID('dbo.TicketEmails', 'U') IS NOT NULL
    PRINT 'OK: TicketEmails table exists.';
ELSE
    PRINT 'ERROR: TicketEmails table was NOT created.';

PRINT '=============================================';
PRINT 'ServiceSphere Email Upgrade complete!';
PRINT '=============================================';
GO
