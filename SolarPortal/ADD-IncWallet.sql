/* ============================================================================
   ADD-IncWallet.sql
   ----------------------------------------------------------------------------
   Run ONCE against the live Solar DB (SSMS). Idempotent - safe to re-run.

   INC wallet ka apna alag ledger hai. Legacy dbo.TrnVoucher / dbo.Vouchertype
   ko HAATH NAHI LAGATE - unka structure copy karke apni tables banayi hain:

       dbo.IncVouchertype   <- dbo.Vouchertype  jaisi
       dbo.IncTrnvoucher    <- dbo.TrnVoucher   jaisi (same columns, same types)

   Kaise chalta hai:
     Installer "Mark Complete" karta hai
       -> us plan ka SolarProjects.IncCommissionAmount uthta hai (flat amount -
          jitna set hai utna hi; koi percentage, koi TDS nahi)
       -> IncCommissionLedger mein audit row banti hai
       -> IncTrnvoucher mein credit row banti hai (AcType = 'I', VType = 'C'),
          CrTo = jo INC user login kiya hai uski Worker Id

     Withdrawal request lagti hai
       -> IncTrnvoucher mein debit row banti hai (VType = 'W'), DrTo = wahi
          Worker Id, amount = jitna request kiya

   Jo pehle se maujood hai (banane ki zaroorat nahi):
     - dbo.SolarProjects.IncCommissionAmount  (per-plan flat commission)
     - dbo.IncCommissionLedger                (commission audit ledger)
   ============================================================================ */

SET NOCOUNT ON;

/* ---- 1. Wallet type master (legacy Vouchertype ka apna version) ---------- */
IF OBJECT_ID('dbo.IncVouchertype', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.IncVouchertype
    (
        Acid         numeric(18,0) IDENTITY(1,1) NOT NULL,
        WalletName   varchar(100)  NOT NULL,
        Actype       char(1)       NOT NULL,
        ActiveStatus char(1)       NOT NULL,
        CONSTRAINT PK_IncVouchertype PRIMARY KEY CLUSTERED (Acid)
    );
    PRINT 'Created dbo.IncVouchertype.';
END
ELSE
    PRINT 'dbo.IncVouchertype already exists.';

IF NOT EXISTS (SELECT 1 FROM dbo.IncVouchertype WHERE Actype = 'I')
    INSERT INTO dbo.IncVouchertype (WalletName, Actype, ActiveStatus)
    VALUES ('INC Wallet', 'I', 'Y');


/* ---- 2. Wallet ledger (legacy TrnVoucher ka apna version) ---------------- */
IF OBJECT_ID('dbo.IncTrnvoucher', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.IncTrnvoucher
    (
        VoucherId    numeric(18,0) IDENTITY(1,1) NOT NULL,
        VoucherNo    numeric(18,0) NOT NULL,
        VoucherDate  datetime      NOT NULL,
        DrTo         varchar(100)  NULL,
        CrTo         varchar(100)  NOT NULL,
        Amount       numeric(18,2) NOT NULL,
        Narration    varchar(2500) NULL,
        RefNo        varchar(150)  NULL,
        AcType       char(1)       NOT NULL,
        RecTimeStamp datetime      NOT NULL,
        VType        char(1)       NOT NULL,
        SessID       numeric(18,0) NOT NULL,
        WSessID      numeric(18,0) NOT NULL,
        Balance      numeric(18,2) NOT NULL,
        UserId       numeric(18,0) NOT NULL,
        FromID       numeric(18,0) NULL,
        CONSTRAINT PK_IncTrnvoucher PRIMARY KEY CLUSTERED (VoucherId)
    );

    -- Wallet statement har baar (AcType, CrTo/DrTo) par filter karta hai.
    CREATE INDEX IX_IncTrnvoucher_AcType_CrTo ON dbo.IncTrnvoucher (AcType, CrTo);
    CREATE INDEX IX_IncTrnvoucher_AcType_DrTo ON dbo.IncTrnvoucher (AcType, DrTo);
    -- Duplicate credit / debit rokne ke liye RefNo par lookup hota hai.
    CREATE INDEX IX_IncTrnvoucher_RefNo      ON dbo.IncTrnvoucher (RefNo);

    PRINT 'Created dbo.IncTrnvoucher.';
END
ELSE
    PRINT 'dbo.IncTrnvoucher already exists.';


/* ---- 3. Handy checks -------------------------------------------------------

   -- Kis plan par kitna commission set hai (khaali = commission nahi milega):
   SELECT Id, Name, SolarTypeKV, ProjectAmount, IncCommissionAmount
   FROM dbo.SolarProjects WHERE IsActive = 1 ORDER BY SolarTypeKV;

   -- Commission set / badalne ke liye:
   -- UPDATE dbo.SolarProjects SET IncCommissionAmount = 600 WHERE Id = 2;

   -- Kisi INC worker ka wallet statement:
   SELECT VoucherNo, VoucherDate, VType, CrTo, DrTo, Amount, Narration, RefNo
   FROM dbo.IncTrnvoucher WHERE AcType = 'I' AND (CrTo = '14' OR DrTo = '14')
   ORDER BY VoucherId DESC;

   -- Wallet balance:
   SELECT ISNULL(SUM(CASE WHEN VType = 'C' AND CrTo = '14' THEN Amount
                          WHEN VType <> 'C' AND DrTo = '14' THEN -Amount
                          ELSE 0 END), 0) AS Balance
   FROM dbo.IncTrnvoucher WHERE AcType = 'I';
   ---------------------------------------------------------------------------- */
