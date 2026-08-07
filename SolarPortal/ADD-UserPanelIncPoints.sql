/* ============================================================================
   ADD-UserPanelIncPoints.sql
   ----------------------------------------------------------------------------
   Run ONCE against the live Solar DB (SSMS) after deploying the user-panel +
   INC-panel changes. Idempotent — safe to re-run; every object is created only
   if it is missing.

   Backs the handwritten-note points that were implemented in the USER panel and
   the INC (installer) panel:

     • point 2  — Only-Solar / Without-Activation IDs may never place a PRODUCT
                  request from the legacy cPanel, only an ACTIVATION request.
                  → SolarRequests.ProductRequestBlocked
     • point 8  — KYC upload for COMMISSION (INC) installers, approved by admin.
                  JOB workers are not asked for KYC.
                  → dbo.IncKycDocuments
     • point 11 — "Mark Installed" takes up to 30 photos, admin sees them and
                  approves/rejects, commission is credited only on approval, and
                  a rejected installation is re-uploaded by the installer.
                  → dbo.InstallationPhotos + new columns on dbo.Installations

   Points 1, 3, 4 and 5 needed NO schema change:
     • point 1 reads the existing legacy TrnProductorderDetail rows
     • points 3 & 4 only changed which stage/approval unlocks a page
     • point 5 is a front-end Camera/Gallery chooser

   No EF migration is generated (per project workflow); EF Core maps these
   columns by convention at runtime once they exist.
   ============================================================================ */

SET NOCOUNT ON;

/* ---- point 2: block product requests for Only-Solar IDs ------------------- */
/* Sticky flag: set the first time a request enters "Only Solar — Without
   Activation" and never cleared, so activating later does not re-open product
   ordering for that ID. The legacy cPanel product-request page must check it:

       SELECT TOP 1 ProductRequestBlocked
       FROM dbo.SolarRequests
       WHERE UserId = @idNo AND IsDeleted = 0
       ORDER BY CreatedAt DESC;                       -- 1 => activation only   */
IF COL_LENGTH('dbo.SolarRequests', 'ProductRequestBlocked') IS NULL
    ALTER TABLE dbo.SolarRequests
        ADD ProductRequestBlocked BIT NOT NULL
            CONSTRAINT DF_SolarRequests_ProductRequestBlocked DEFAULT(0);
GO

/* Backfill: any EXISTING request that started as (or currently is) Only Solar —
   Without Activation is blocked too, including ones already upgraded via
   "Activate Now". RequestType / OriginalRequestType 2 = OnlySolarWithoutActivation. */
UPDATE dbo.SolarRequests
SET    ProductRequestBlocked = 1
WHERE  ProductRequestBlocked = 0
  AND (RequestType = 2 OR OriginalRequestType = 2 OR WithoutActivationOn IS NOT NULL);
GO

/* ---- point 11: admin verification of a marked installation --------------- */
/* ApprovalStatus: 1 = Pending, 2 = Approved, 3 = Rejected (ApprovalStatus enum).
   Existing completed rows are back-filled to Approved + CommissionCredited so
   installations that were already paid under the OLD "credit on mark-complete"
   rule are not re-queued for approval or paid a second time. */
IF COL_LENGTH('dbo.Installations', 'ApprovalStatus') IS NULL
    ALTER TABLE dbo.Installations
        ADD ApprovalStatus INT NOT NULL
            CONSTRAINT DF_Installations_ApprovalStatus DEFAULT(1);
GO

IF COL_LENGTH('dbo.Installations', 'RejectionReason') IS NULL
    ALTER TABLE dbo.Installations ADD RejectionReason NVARCHAR(1000) NULL;

IF COL_LENGTH('dbo.Installations', 'ReviewedAt') IS NULL
    ALTER TABLE dbo.Installations ADD ReviewedAt DATETIME2 NULL;

IF COL_LENGTH('dbo.Installations', 'ReviewedBy') IS NULL
    ALTER TABLE dbo.Installations ADD ReviewedBy NVARCHAR(450) NULL;

IF COL_LENGTH('dbo.Installations', 'SubmittedAt') IS NULL
    ALTER TABLE dbo.Installations ADD SubmittedAt DATETIME2 NULL;

IF COL_LENGTH('dbo.Installations', 'CommissionCredited') IS NULL
    ALTER TABLE dbo.Installations
        ADD CommissionCredited BIT NOT NULL
            CONSTRAINT DF_Installations_CommissionCredited DEFAULT(0);
GO

/* Back-fill only rows that were completed BEFORE this change shipped. They were
   credited at mark-complete time under the old rule, so they are treated as
   already approved and already paid. Guarded on SubmittedAt IS NULL so re-running
   this script never touches installations submitted under the new flow. */
UPDATE dbo.Installations
SET    ApprovalStatus     = 2,           -- Approved
       CommissionCredited = 1,
       ReviewedAt         = ISNULL(ReviewedAt, CompletedAt)
WHERE  IsCompleted = 1
  AND  SubmittedAt IS NULL
  AND  ApprovalStatus = 1;
GO

/* ---- point 11: the photo set -------------------------------------------- */
IF OBJECT_ID('dbo.InstallationPhotos', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InstallationPhotos
    (
        Id                 INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_InstallationPhotos PRIMARY KEY,
        InstallationId     INT            NOT NULL,
        SolarRequestId     INT            NOT NULL,
        FilePath           NVARCHAR(500)  NOT NULL,
        FileName           NVARCHAR(255)  NULL,
        ContentType        NVARCHAR(100)  NULL,
        FileSizeBytes      BIGINT         NOT NULL CONSTRAINT DF_InstallationPhotos_Size DEFAULT(0),
        UploadedByWorkerId INT            NULL,

        /* BaseEntity columns — same shape as every other table in this schema. */
        CreatedAt          DATETIME2      NOT NULL CONSTRAINT DF_InstallationPhotos_CreatedAt DEFAULT(GETUTCDATE()),
        CreatedBy          NVARCHAR(450)  NULL,
        UpdatedAt          DATETIME2      NULL,
        UpdatedBy          NVARCHAR(450)  NULL,
        IsDeleted          BIT            NOT NULL CONSTRAINT DF_InstallationPhotos_IsDeleted DEFAULT(0),

        CONSTRAINT FK_InstallationPhotos_Installations
            FOREIGN KEY (InstallationId) REFERENCES dbo.Installations(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_InstallationPhotos_InstallationId ON dbo.InstallationPhotos(InstallationId);
    CREATE INDEX IX_InstallationPhotos_SolarRequestId ON dbo.InstallationPhotos(SolarRequestId);
END
GO

/* Carry the old single completion photo into the new set so admin sees a photo
   for installations marked complete before this change. */
INSERT INTO dbo.InstallationPhotos
        (InstallationId, SolarRequestId, FilePath, FileName, FileSizeBytes, UploadedByWorkerId, CreatedAt, IsDeleted)
SELECT   i.Id, i.SolarRequestId, i.CompletionPhotoPath, 'completion', 0, i.AssignedWorkerId, ISNULL(i.CompletedAt, GETUTCDATE()), 0
FROM     dbo.Installations i
WHERE    i.CompletionPhotoPath IS NOT NULL
  AND    LTRIM(RTRIM(i.CompletionPhotoPath)) <> ''
  AND    NOT EXISTS (SELECT 1 FROM dbo.InstallationPhotos p WHERE p.InstallationId = i.Id);
GO

/* ---- point 8: INC KYC ---------------------------------------------------- */
/* One row per INC worker, shaped like the existing member-panel KYC page
   (legacy KYC.aspx): three independently-verified sections — Address Proof,
   Bank Detail, PAN Card.

   *Status columns: 1 = Pending ("Verification Due"), 2 = Approved, 3 = Rejected.
   Admin sets the status + remark from the ADMIN app; a section left at 3 unlocks
   in the INC panel so the installer can correct it.

   Rows only ever exist for Workers whose Type = 2 (INC) — JOB workers are never
   asked for KYC. */
IF OBJECT_ID('dbo.IncKycDocuments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.IncKycDocuments
    (
        Id                    INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_IncKycDocuments PRIMARY KEY,
        WorkerId              INT            NOT NULL,

        /* -- Section 1: Address Proof -- */
        Address               NVARCHAR(250)  NULL,
        PinCode               NVARCHAR(10)   NULL,
        StateCode             NVARCHAR(20)   NULL,   -- M_StateDivMaster.StateCode
        StateName             NVARCHAR(100)  NULL,
        District              NVARCHAR(100)  NULL,
        City                  NVARCHAR(100)  NULL,
        IdProofTypeId         INT            NULL,   -- M_IdTypeMaster.Id
        IdProofTypeName       NVARCHAR(100)  NULL,
        IdProofNo             NVARCHAR(30)   NULL,
        AddressProofFrontPath NVARCHAR(500)  NULL,
        AddressProofBackPath  NVARCHAR(500)  NULL,
        AddressStatus         INT            NOT NULL CONSTRAINT DF_IncKyc_AddressStatus DEFAULT(1),
        AddressRemark         NVARCHAR(1000) NULL,
        AddressReviewedAt     DATETIME2      NULL,
        AddressReviewedBy     NVARCHAR(450)  NULL,

        /* -- Section 2: Bank Detail -- */
        AccountType           NVARCHAR(50)   NULL,
        AccountNo             NVARCHAR(30)   NULL,
        BankId                INT            NULL,   -- M_BankMaster.BId
        BankName              NVARCHAR(150)  NULL,
        BranchName            NVARCHAR(150)  NULL,
        IfscCode              NVARCHAR(20)   NULL,
        BankProofPath         NVARCHAR(500)  NULL,
        BankStatus            INT            NOT NULL CONSTRAINT DF_IncKyc_BankStatus DEFAULT(1),
        BankRemark            NVARCHAR(1000) NULL,
        BankReviewedAt        DATETIME2      NULL,
        BankReviewedBy        NVARCHAR(450)  NULL,

        /* -- Section 3: PAN Card -- */
        PanNo                 NVARCHAR(15)   NULL,
        PanProofPath          NVARCHAR(500)  NULL,
        PanStatus             INT            NOT NULL CONSTRAINT DF_IncKyc_PanStatus DEFAULT(1),
        PanRemark             NVARCHAR(1000) NULL,
        PanReviewedAt         DATETIME2      NULL,
        PanReviewedBy         NVARCHAR(450)  NULL,

        SubmittedAt           DATETIME2      NULL,

        CreatedAt             DATETIME2      NOT NULL CONSTRAINT DF_IncKycDocuments_CreatedAt DEFAULT(GETUTCDATE()),
        CreatedBy             NVARCHAR(450)  NULL,
        UpdatedAt             DATETIME2      NULL,
        UpdatedBy             NVARCHAR(450)  NULL,
        IsDeleted             BIT            NOT NULL CONSTRAINT DF_IncKycDocuments_IsDeleted DEFAULT(0),

        CONSTRAINT FK_IncKycDocuments_Workers
            FOREIGN KEY (WorkerId) REFERENCES dbo.Workers(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_IncKycDocuments_WorkerId ON dbo.IncKycDocuments(WorkerId);
END
GO

PRINT 'User-panel + INC-panel objects are present (created / added where missing).';
