/* ============================================================================
   ADD-RemainingBv.sql
   ----------------------------------------------------------------------------
   Schema for "Update Remaining BV" - the member page in SolarPortal.Web and the
   verification page in SolarPortal.AdminWeb.

   Run ONCE against the live Solar DB (SSMS). Idempotent - safe to re-run; every
   object is created only if it is missing.

   No EF migration is generated (per project workflow - same as
   ADD-UserPanelIncPoints.sql); EF Core maps the table by convention at runtime
   once it exists.

   FLOW
   ----
     1. Admin approves the member's solar request
        (dbo.SolarRequests.ApprovalStatus = 2) AND the member has a VERIFIED
        product order in TrnProductorderDetail (IsApprove = 'Y'). By then the
        product is taken, so the BV is known and fixed.
     2. "Update Remaining BV" opens in the member panel:
          * Sponsor ID    - automatic (m_membermaster.RefFormNo -> sponsor IdNo)
          * Discom Income - Self / Other. An "Other" ID is verified to be ABOVE
                            the member in the SPONSOR tree, never below.
          * Deal Close    - same rule
          * SCI Income    - always the sponsor's IdNo
        Each head also DISPLAYS the money it pays, copied from SolarProjects:
            DiscomWork -> Discom Income, DealClose -> Deal Close,
            SCZMenue   -> SCI Income
        The member picks WHO receives each head, never how much.
     3. Save => Status 1 (Pending). The record shows in the admin panel, where the
        admin can correct it and then either APPROVE (Status 2) or REJECT (3).
     4. Rejected goes back to the member with a reason. They fix it and save
        again onto the SAME row - the filtered unique index below is what keeps a
        re-submission from creating a duplicate.
     5. Approved freezes the row for everyone. That day the income is added to the
        daily payout, and the payout run stamps PayoutPostedAt so it is never
        paid twice.

   BV maths
   --------
     TotalBV     = SolarProjects.FinalBV of the selected plan (110 / 175)
     FixedBV     = bv on the verified TrnProductorderDetail row (100), already
                   credited by the normal product flow
     RemainingBV = TotalBV - FixedBV, and that is what gets distributed
   ============================================================================ */

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.RemainingBvUpdates', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RemainingBvUpdates
    (
        Id                  INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_RemainingBvUpdates PRIMARY KEY,
        SolarRequestId      INT             NOT NULL,
        RequestNumber       NVARCHAR(30)    NULL,

        /* Request owner, as the legacy m_membermaster knows them */
        MemberIdNo          NVARCHAR(50)    NOT NULL,
        MemberName          NVARCHAR(150)   NULL,
        MemberFormNo        DECIMAL(18,0)   NOT NULL CONSTRAINT DF_RemainingBv_MemberFormNo DEFAULT(0),

        /* Auto-resolved - never typed in by the member */
        SponsorIdNo         NVARCHAR(50)    NULL,
        SponsorName         NVARCHAR(150)   NULL,

        /* The plan taken, from the SolarProjects master - the "which KV" the
           member recognises. This is what the screens show. */
        PlanName            NVARCHAR(150)   NULL,
        SolarTypeKV         DECIMAL(8,2)    NOT NULL CONSTRAINT DF_RemainingBv_KV DEFAULT(0),

        /* The verified product order, kept for audit against the legacy order */
        OrderNo             NVARCHAR(50)    NULL,
        ProductName         NVARCHAR(250)   NULL,

        /* BV snapshot taken at submit time, so an approved payout stays
           reproducible even if the plan or product is re-priced later */
        TotalBV             DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_TotalBV     DEFAULT(0),
        FixedBV             DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_FixedBV     DEFAULT(100),
        RemainingBV         DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_RemainingBV DEFAULT(0),
        BvSource            NVARCHAR(250)   NULL,

        /* Income heads. Mode: 1 = Self, 2 = Other (BvBeneficiaryMode enum).
           SCI Income has no mode - it is always the sponsor.
           The *Amount columns are a snapshot of the SolarProjects master and are
           display only; nobody types them. */
        DiscomIncomeMode    INT             NOT NULL CONSTRAINT DF_RemainingBv_DiscomMode DEFAULT(1),
        DiscomIncomeIdNo    NVARCHAR(50)    NULL,
        DiscomIncomeName    NVARCHAR(150)   NULL,
        DiscomIncomeAmount  DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_DiscomAmt DEFAULT(0),

        DealCloseMode       INT             NOT NULL CONSTRAINT DF_RemainingBv_DealMode DEFAULT(1),
        DealCloseIdNo       NVARCHAR(50)    NULL,
        DealCloseName       NVARCHAR(150)   NULL,
        DealCloseAmount     DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_DealAmt DEFAULT(0),

        SciIncomeIdNo       NVARCHAR(50)    NULL,
        SciIncomeName       NVARCHAR(150)   NULL,
        SciIncomeAmount     DECIMAL(18,2)   NOT NULL CONSTRAINT DF_RemainingBv_SciAmt DEFAULT(0),

        /* ApprovalStatus enum: 1 = Pending, 2 = Approved (frozen), 3 = Rejected */
        Status              INT             NOT NULL CONSTRAINT DF_RemainingBv_Status DEFAULT(1),
        SubmittedAt         DATETIME2       NULL,

        /* Admin trail */
        AdminRemark         NVARCHAR(1000)  NULL,
        RejectionReason     NVARCHAR(1000)  NULL,
        CorrectedAt         DATETIME2       NULL,
        CorrectedBy         NVARCHAR(450)   NULL,
        RejectedAt          DATETIME2       NULL,
        RejectedBy          NVARCHAR(450)   NULL,
        ApprovedAt          DATETIME2       NULL,
        ApprovedBy          NVARCHAR(450)   NULL,

        /* Stamped by the daily payout run so a row can never be paid twice */
        PayoutPostedAt      DATETIME2       NULL,

        /* BaseEntity columns - same shape as every other table in this schema */
        CreatedAt           DATETIME2       NOT NULL CONSTRAINT DF_RemainingBv_CreatedAt DEFAULT(GETUTCDATE()),
        CreatedBy           NVARCHAR(450)   NULL,
        UpdatedAt           DATETIME2       NULL,
        UpdatedBy           NVARCHAR(450)   NULL,
        IsDeleted           BIT             NOT NULL CONSTRAINT DF_RemainingBv_IsDeleted DEFAULT(0),

        CONSTRAINT FK_RemainingBvUpdates_SolarRequests
            FOREIGN KEY (SolarRequestId) REFERENCES dbo.SolarRequests(Id) ON DELETE CASCADE
    );

    /* One live row per request. This is what makes a re-submission after a
       rejection update the existing row instead of inserting a duplicate.
       Filtered on IsDeleted so a soft-deleted row never blocks a fresh one. */
    CREATE UNIQUE INDEX UX_RemainingBvUpdates_SolarRequestId
        ON dbo.RemainingBvUpdates(SolarRequestId)
        WHERE IsDeleted = 0;

    CREATE INDEX IX_RemainingBvUpdates_MemberIdNo
        ON dbo.RemainingBvUpdates(MemberIdNo);

    /* The daily payout run reads this: approved, not yet posted. */
    CREATE INDEX IX_RemainingBvUpdates_Payout
        ON dbo.RemainingBvUpdates(Status, PayoutPostedAt)
        WHERE IsDeleted = 0;
END
GO

PRINT 'dbo.RemainingBvUpdates is present (created where missing).';
GO


/* ============================================================================
   Where the per-head money comes from - the plan master.
   ============================================================================ */
/*
    SELECT DiscomWork AS [Discom Income],
           DealClose  AS [Deal Close],
           SCZMenue   AS [SCI Income],
           *
    FROM   dbo.SolarProjects
    WHERE  IsActive = 1;
*/


/* ============================================================================
   DAILY PAYOUT hand-off - the income becomes payable on the day it is approved.
   After crediting, stamp PayoutPostedAt on the same Ids so nothing is paid twice.
   ============================================================================ */
/*
    SELECT  r.Id, r.MemberIdNo, r.RequestNumber, r.RemainingBV,
            r.DiscomIncomeIdNo, r.DiscomIncomeAmount,
            r.DealCloseIdNo,    r.DealCloseAmount,
            r.SciIncomeIdNo,    r.SciIncomeAmount,
            r.ApprovedAt
    FROM    dbo.RemainingBvUpdates r
    WHERE   r.IsDeleted = 0
      AND   r.Status = 2               -- Approved
      AND   r.PayoutPostedAt IS NULL;

    UPDATE dbo.RemainingBvUpdates
    SET    PayoutPostedAt = GETUTCDATE()
    WHERE  Id IN ( ... );
*/
