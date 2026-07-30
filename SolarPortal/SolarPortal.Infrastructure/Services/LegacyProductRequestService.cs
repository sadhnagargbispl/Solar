using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Inserts "With Activation" submissions into the legacy SolFit table
/// TrnProductorderDetail via raw ADO.NET, mirroring the original VB
/// page ProductWalletRequest.aspx.vb (tp=A branch).
///
/// Reads the picked product's master row from [V#SpProductDetail] to get
/// MRP/DP/BV/PV/ShippingAmount/ProductName/ShippingProdid — same view the
/// new "With Activation" UI uses for the product cards. The insert mirrors
/// the legacy column list exactly so existing reports keep working.
///
/// We do NOT fail the user submission if the legacy insert errors — the
/// new SolarRequest record is the source of truth for the new workflow.
/// Legacy bridge errors are logged and surfaced as a warning so admin can
/// reconcile manually if needed.
/// </summary>
public class LegacyProductRequestService : ILegacyProductRequestService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LegacyProductRequestService> _log;

    public LegacyProductRequestService(
        IConfiguration config,
        ApplicationDbContext db,
        ILogger<LegacyProductRequestService> log)
    {
        _config = config;
        _db     = db;
        _log    = log;
    }

    public async Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input)
    {
        var result = new LegacyInsertResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy bridge.";
            return result;
        }
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            long formNo;
            await using (var cmdF = new SqlCommand(
                "SELECT TOP 1 Formno FROM M_MemberMaster WHERE Idno = @id", conn))
            {
                cmdF.Parameters.AddWithValue("@id", input.MemberIdNo ?? string.Empty);
                var fObj = await cmdF.ExecuteScalarAsync();
                if (fObj == null || fObj == DBNull.Value)
                {
                    result.ErrorMessage = $"Legacy bridge: member IdNo '{input.MemberIdNo}' not found in M_MemberMaster.";
                    _log.LogWarning(result.ErrorMessage);
                    return result;
                }
                formNo = Convert.ToInt64(fObj);
            }
            string? orderNo = null;
            var rng = new Random();
            for (int i = 0; i < 5; i++)
            {
                var candidate = $"{rng.Next(100000, 999999)}{formNo}";
                await using var cmdC = new SqlCommand(
                    "SELECT COUNT(*) FROM TrnOrder WHERE Orderno = @o", conn);
                cmdC.Parameters.AddWithValue("@o", candidate);
                var cnt = (int)(await cmdC.ExecuteScalarAsync() ?? 0);
                if (cnt == 0) { orderNo = candidate; break; }
            }
            if (orderNo == null)
            {
                result.ErrorMessage = "Legacy bridge: couldn't allocate a unique OrderNo after 5 attempts.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }
            const string insertSql = @"
INSERT INTO TrnProductorderDetail
    (OrderNo, FormNo, ProductID, Qty, Rate, NetAmount, RecTimeStamp,
     DispDate, DispStatus, DispQty, RemQty, DispAmt,
     MRP, DP, ProductName, ImgPath, RP, BV,
     FSEssId, Prodtype, PV, txnid, txndate, ImageUpload,
     ForType, PID,
     UserAddress, City, District, PinCode, UserState, StateCode, IsApprove)
SELECT
    @OrderNo, @FormNo, ProdId, @Qty, DP, DP * @Qty, GETDATE(),
    NULL, 'N', 0, @Qty, 0,
    MRP, DP, ProductName, '', 0, BV,
    (SELECT ISNULL(MAX(FsessID), 1) FROM solfitenergyinv..M_FiscalMaster),
    'P', PV, @TxnId, @TxnDate, @ImageFile,
    'A', @PayMode,
    @Addr, @City, @Dist, @Pin, @StateNm, @StateCd, 'N'
FROM [V#SpProductDetail]
WHERE ProdId = @ProdId;";

            await using var cmdI = new SqlCommand(insertSql, conn);
            cmdI.Parameters.AddWithValue("@OrderNo",   orderNo);
            cmdI.Parameters.AddWithValue("@FormNo",    formNo);
            cmdI.Parameters.AddWithValue("@ProdId",    input.ProductId);
            cmdI.Parameters.AddWithValue("@Qty",       Math.Max(1, input.Qty));
            cmdI.Parameters.AddWithValue("@TxnId",     (object?)input.TxnId ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@TxnDate",   (object?)input.TxnDate ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@ImageFile", (object?)input.ImageFileName ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@PayMode",   input.PayModeId);
            cmdI.Parameters.AddWithValue("@Addr",      (object?)input.Address ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@City",      (object?)input.City ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@Dist",      (object?)input.District ?? DBNull.Value);
            object pinParam = DBNull.Value;
            if (!string.IsNullOrWhiteSpace(input.PinCode))
            {
                var pinDigits = new string(input.PinCode.Where(char.IsDigit).ToArray());
                if (pinDigits.Length > 0 &&
                    decimal.TryParse(pinDigits, System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out var pinVal))
                {
                    pinParam = pinVal;
                }
            }
            cmdI.Parameters.Add(new SqlParameter("@Pin", System.Data.SqlDbType.Decimal)
            {
                Precision = 18,
                Scale     = 0,
                Value     = pinParam
            });
            cmdI.Parameters.AddWithValue("@StateNm",   (object?)input.StateName ?? DBNull.Value);
            object stateCodeParam = await ResolveStateCodeAsync(input.StateCode, input.StateName);
            var stateCdSqlParam = new SqlParameter("@StateCd", System.Data.SqlDbType.Decimal)
            {
                Precision = 18,
                Scale     = 0,
                Value     = stateCodeParam
            };
            cmdI.Parameters.Add(stateCdSqlParam);

            var rows = await cmdI.ExecuteNonQueryAsync();
            if (rows < 1)
            {
                result.ErrorMessage = $"Legacy bridge: product Id {input.ProductId} not found in V#SpProductDetail.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }

            result.Success = true;
            result.OrderNo = orderNo;
            _log.LogInformation("Legacy product order created: OrderNo={OrderNo}, FormNo={FormNo}, ProductId={ProdId}, Qty={Qty}",orderNo, formNo, input.ProductId, input.Qty);
            if (input.AutoProcessApproval)
            {
                // Amount drives the kit (TopUp) lookup (BV <= @Amount) and the
                // TrnPaymentConfirmation.OrderAmt row, so it must be the ORDER
                // amount (DP x Qty) - not the part-payment the user just made.
                // Read it back off the row we just inserted unless the caller
                // supplied one explicitly.
                var approvalAmount = input.Amount;
                if (approvalAmount <= 0)
                {
                    await using var cmdA = new SqlCommand(
                        "SELECT ISNULL(SUM(NetAmount), 0) FROM TrnProductorderDetail WHERE OrderNo = @o", conn);
                    cmdA.Parameters.AddWithValue("@o", orderNo);
                    approvalAmount = Convert.ToDecimal(await cmdA.ExecuteScalarAsync() ?? 0m);
                }

                var approvalInput = new LegacyApprovalInput
                {
                    OrderNo = orderNo,
                    FormNo = formNo.ToString(),
                    MemberIdNo = input.MemberIdNo ?? string.Empty,
                    ReqNo = !string.IsNullOrWhiteSpace(input.ReqNo) ? input.ReqNo : (input.TxnId ?? string.Empty),
                    Amount = approvalAmount,
                    PayMode = input.PayModeName ?? string.Empty,
                    ActType = string.IsNullOrWhiteSpace(input.ActType) ? "A" : input.ActType,
                    ApproveType = input.ApproveType ?? "Y",
                    ApproveRemark = input.ApproveRemark ?? string.Empty,
                    PartyCode = input.PartyCode ?? string.Empty,
                    InvDatabaseName = string.IsNullOrWhiteSpace(input.InvDatabaseName)
                                          ? "solfitenergyinv"
                                          : input.InvDatabaseName
                };

                var approvalResult = await ApproveOrRejectProductRequestAsync(approvalInput);
                result.ApprovalResult = approvalResult;

                if (!approvalResult.Success)
                {
                    _log.LogWarning(
                        "Legacy bridge: insert for OrderNo={OrderNo} succeeded but auto-approval failed: {Error}",
                        orderNo, approvalResult.ErrorMessage);

                    // Surface it. From the caller's point of view "sync" means
                    // insert + approve: if approval fails the row sits at
                    // IsApprove='N' with no TrnOrder header and the member never
                    // activates. Reporting Success=true here made these failures
                    // completely invisible in the UI.
                    result.Success = false;
                    result.ErrorMessage =
                        $"OrderNo {orderNo} inserted, but approval failed: {approvalResult.ErrorMessage}";
                }
            }

            return result;
            //  return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Legacy bridge insert failed for IdNo={IdNo}, ProductId={ProdId}",
                input.MemberIdNo, input.ProductId);
            result.ErrorMessage = $"Legacy bridge insert failed: {ex.Message}";
            return result;
        }
    }
    private async Task<object> ResolveStateCodeAsync(string? rawCode, string? stateName)
    {
        // 1. Did the caller already give us a numeric code?
        if (!string.IsNullOrWhiteSpace(rawCode) &&
            decimal.TryParse(rawCode.Trim(), System.Globalization.NumberStyles.Number,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var parsed))
        {
            return parsed;
        }

        // 2. Lookup by state name in M_StateDivMaster
        if (!string.IsNullOrWhiteSpace(stateName))
        {
            var name = stateName.Trim();
            var row = await _db.States
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StateName != null && s.StateName.Trim() == name);

            if (row != null) return row.StateCode;

            // Try case-insensitive match as fallback (state names sometimes
            // arrive with different casing from the dropdown vs DB)
            var allStates = await _db.States.AsNoTracking().ToListAsync();
            var ci = allStates.FirstOrDefault(s =>
                s.StateName != null &&
                string.Equals(s.StateName.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci.StateCode;

            _log.LogWarning("Legacy bridge: state name '{StateName}' not found in M_StateDivMaster; sending NULL StateCode.",
                stateName);
        }

        // 3. Give up — let SQL store NULL rather than blow up on type conversion
        return DBNull.Value;
    }
    public async Task<LegacyApprovalResult> ApproveOrRejectProductRequestAsync(LegacyApprovalInput input)
    {
        var result = new LegacyApprovalResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy bridge.";
            return result;
        }

        // InvDatabaseName is used as a raw identifier (can't be a SQL parameter),
        // so it must be strictly validated first.
        string invDb;
        try
        {
            invDb = SanitizeIdentifier(input.InvDatabaseName);
        }
        catch (ArgumentException ex)
        {
            result.ErrorMessage = $"Legacy bridge: invalid InvDatabaseName. {ex.Message}";
            return result;
        }

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        try
        {
            // ─── 1. Duplicate-processing guard ───────────────────────────
            // VB: Select Count(*) from TrnProductorderDetail where orderno = ...
            //     and IsApprove <> 'N'  -> if 0, this order hasn't been
            //     approved/rejected yet, so proceed.
            int alreadyProcessedCount;
            await using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM TrnProductorderDetail WHERE OrderNo = @OrderNo AND IsApprove <> 'N'",
                conn, tx))
            {
                cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
                alreadyProcessedCount = (int)(await cmd.ExecuteScalarAsync() ?? 0);
            }
            if (alreadyProcessedCount > 0)
            {
                result.AlreadyProcessed = true;
                result.ErrorMessage = $"Order {input.OrderNo} was already approved/rejected.";
                await tx.RollbackAsync();
                return result;
            }

            // ─── 2. Optional kit (TopUp) lookup when ActType = "A" ───────
            // VB: select KitId,KitName from m_Kitmaster where TopUpSeq > 0
            //     and BV <= Amount and BV > 0 and ActiveStatus='Y'
            //     order by KitAmount desc
            string kitName = string.Empty;
            if (string.Equals(input.ActType, "A", StringComparison.OrdinalIgnoreCase))
            {
                await using var cmd = new SqlCommand(
                    "SELECT TOP 1 KitId, KitName FROM m_Kitmaster " +
                    "WHERE TopUpSeq > 0 AND BV <= @Amount AND BV > 0 AND ActiveStatus = 'Y' " +
                    "ORDER BY KitAmount DESC", conn, tx);
                cmd.Parameters.AddWithValue("@Amount", input.Amount);
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    kitName = rdr["KitName"]?.ToString() ?? string.Empty;
                }
            }

            // ─── 3. Resolve Sessid ────────────────────────────────────────
            // VB: sum(RepurchaseWallet) from ufnGetBalance(FormNo,'R')
            //     union with max(Sessid) from M_SessnMaster
            long sessid = 0;
            await using (var cmd = new SqlCommand(
                "SELECT SUM(RepurchaseWallet) AS RepurchaseWallet, SUM(Sessid) AS Sessid FROM (" +
                "  SELECT Balance AS RepurchaseWallet, 0 AS Sessid FROM dbo.ufnGetBalance(@FormNo, 'R') " +
                "  UNION ALL " +
                "  SELECT 0 AS RepurchaseWallet, MAX(Sessid) AS Sessid FROM M_SessnMaster" +
                ") AS Temp", conn, tx))
            {
                cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync() && rdr["Sessid"] != DBNull.Value)
                {
                    sessid = Convert.ToInt64(rdr["Sessid"]);
                }
            }

            // ─── 4. Member lookup ─────────────────────────────────────────
            // VB: Select Address1, ActiveStatus, Kitid from M_MemberMaster
            //     where Formno = ...
            string productStatus = string.Empty;
            string productKitId = string.Empty;
            await using (var cmd = new SqlCommand(
                "SELECT Address1, ActiveStatus, Kitid FROM M_MemberMaster WHERE Formno = @FormNo",
                conn, tx))
            {
                cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    productStatus = rdr["ActiveStatus"]?.ToString() ?? string.Empty;
                    productKitId = rdr["Kitid"]?.ToString() ?? string.Empty;
                }
            }

            string orderType = productStatus == "Y" ? "O" : "T";

            // VB always leaves TotalPV at 0 (dead variable in the original
            // code — ds1 is fetched but never summed into it). Preserved as-is.
            const decimal totalPv = 0m;

            if (string.Equals(input.ApproveType, "Y", StringComparison.OrdinalIgnoreCase))
            {
                var remark = $"Approve Payment Request On ReqNo:{input.ReqNo} for Idno:{input.MemberIdNo}";

                if (string.Equals(input.PayMode, "Cash", StringComparison.OrdinalIgnoreCase))
                {
                    await ApproveCashPathAsync(conn, tx, input, sessid, productStatus, productKitId, totalPv, remark);
                }
                else
                {
                    await ApproveNonCashPathAsync(conn, tx, input, sessid, productStatus, productKitId, totalPv,
                        remark, kitName, orderType, invDb);
                }
            }
            else
            {
                // ─── Reject path ──────────────────────────────────────────
                // VB: Update TrnProductorderDetail Set IsApprove = 'R', ...
                var remark = $"Reject Payment Request On ReqNo:{input.ReqNo} for Idno:{input.MemberIdNo}";
                await using var cmd = new SqlCommand(
                    "UPDATE TrnProductorderDetail SET IsApprove = 'R', Remark = @Remark, " +
                    "ApproveRemark = @ApproveRemark, Rejectdate = GETDATE() WHERE OrderNo = @OrderNo",
                    conn, tx);
                cmd.Parameters.AddWithValue("@Remark", remark);
                cmd.Parameters.AddWithValue("@ApproveRemark", input.ApproveRemark ?? string.Empty);
                cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            result.Success = true;
            _log.LogInformation("Legacy approval processed: OrderNo={OrderNo}, ApproveType={ApproveType}",
                input.OrderNo, input.ApproveType);
            return result;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _log.LogError(ex, "Legacy bridge approval failed for OrderNo={OrderNo}", input.OrderNo);
            result.ErrorMessage = $"Legacy bridge approval failed: {ex.Message}";
            return result;
        }
    }
    private async Task ApproveCashPathAsync(SqlConnection conn, SqlTransaction tx, LegacyApprovalInput input,long sessid, string productStatus, string productKitId, decimal totalPv, string remark)
    {
        // Existing PVvalue already posted for this member (used for the
        // activation threshold check together with TotalPV).
        decimal existingPvValue = await GetExistingPvValueAsync(conn, tx, input.FormNo);

        // Aggregate BV/PV of the pending order-detail rows.
        var (bv, pv) = await GetOrderBvPvAsync(conn, tx, input.OrderNo);

        if (productStatus == "N")
        {
            await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                repurchincome: bv, billType: "A", soldBy: input.PartyCode, pvValue: pv, fromId: input.FormNo);

            await MaybeActivateMemberAsync(conn, tx, input.MemberIdNo, input.OrderNo,
                existingPvValue + totalPv, bv);
        }
        else if (productStatus == "Y")
        {
            if (productKitId == "5")
            {
                await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                    repurchincome: 0, billType: "A", soldBy: input.PartyCode, pvValue: pv, fromId: null);

                // NOTE: VB only calls activation here for the >=1975 branches
                // (no "else" clause), matching the original exactly.
                await MaybeActivateMemberAsync(conn, tx, input.MemberIdNo, input.OrderNo,
                    existingPvValue + totalPv, bv, onlyThresholdBranches: true);
            }
            else
            {
                await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                    repurchincome: bv, billType: "R", soldBy: input.PartyCode, pvValue: pv, fromId: input.FormNo);
                // No activation call in this branch, matching VB.
            }
        }

        await using var upd = new SqlCommand(
            "UPDATE TrnProductorderDetail SET IsApprove = 'Y', Remark = @Remark, " +
            "ApproveRemark = @ApproveRemark, Approvedate = GETDATE() WHERE OrderNo = @OrderNo",
            conn, tx);
        upd.Parameters.AddWithValue("@Remark", remark);
        upd.Parameters.AddWithValue("@ApproveRemark", input.ApproveRemark ?? string.Empty);
        upd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
        await upd.ExecuteNonQueryAsync();
    }
    private async Task ApproveNonCashPathAsync(SqlConnection conn, SqlTransaction tx, LegacyApprovalInput input,long sessid, string productStatus, string productKitId, decimal totalPv,string remark, string kitName, string orderType, string invDb)
    {
        // 1) Copy each pending TrnProductorderDetail row into TrnorderDetail,
        //    pulling fresh MRP/DP/BV/PV/ProductName from the linked product
        //    master (invDb is a validated identifier, safe to interpolate).
        await using (var cmd = new SqlCommand($@"
INSERT INTO TrnorderDetail
    (OrderNo, FormNo, ProductID, Qty, Rate, NetAmount, RecTimeStamp, DispDate, DispStatus,
     DispQty, RemQty, DispAmt, MRP, DP, ProductName, ImgPath, RP, BV, FSEssId, Prodtype, PV, UserTypeAD)
SELECT @OrderNo, @FormNo, pom.ProdId, d.Qty, pom.DP, pom.DP * d.Qty, GETDATE(), '', 'N', 0, d.Qty,
       0, pom.MRP, pom.DP, pom.ProductName, '', 0, pom.BV,
       (SELECT ISNULL(MAX(FsessID), 1) FROM {invDb}..M_FiscalMaster), 'P', pom.PV, 'W'
FROM TrnProductorderDetail d
JOIN {invDb}..M_ProductMaster pom
     ON pom.ProdId = d.ProductID AND pom.ActiveStatus = 'Y' AND pom.OnWebsite = 'Y'
WHERE d.OrderNo = @OrderNo AND d.IsApprove = 'N';", conn, tx))
        {
            cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
            cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2) Create the TrnOrder header from M_MemberMaster.
        await using (var cmd = new SqlCommand(@"
INSERT INTO TrnOrder
    (OrderNo, OrderDate, MemFirstName, MemLastName, Address1, Address2, CountryID, CountryName,
     StateCode, City, PinCode, Mobl, EMail, FormNo, UserType, Passw, PayMode, ChDDNo, ChDate, ChAmt,
     BankName, BranchName, Remark, OrderAmt, OrderItem, OrderQty, ActiveStatus, HostIp, RecTimeStamp,
     IsTransfer, DispatchDate, DispatchStatus, DispatchQty, RemainQty, DispatchAmount, Shipping, SessID,
     RewardPoint, CourierName, DocketNo, OrderFor, IsConfirm, OrderType, Discount, OldShipping,
     ShippingStatus, IdNo, FSessId, BankAmt, OtherAmt, WalletAmt, TravelPoint, KitName, ForVadicGurukul)
SELECT @OrderNo, CAST(CONVERT(varchar, GETDATE(), 106) AS datetime), MemFirstName, MemLastName,
       Address1, Address2, CountryID, CountryName, StateCode, City,
       CASE WHEN PinCode = '' THEN 0 ELSE PinCode END, Mobl, EMail, @FormNo, '', Passw, '', 0, '', 0,
       '', '', @ApproveRemark, 0, 0, 0, 'Y', 'H', GETDATE(), 'Y', '', 'N', 0, 0, 0,
       0, @Sessid, 0, '', 0, '', 'Y', @OrderType, 0, 0, 'Y', @MemberIdNo, 1, 0, 0, 0, 0, @KitName, 'N'
FROM M_MemberMaster WHERE Formno = @FormNo;", conn, tx))
        {
            cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
            cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
            cmd.Parameters.AddWithValue("@ApproveRemark", input.ApproveRemark ?? string.Empty);
            cmd.Parameters.AddWithValue("@Sessid", sessid);
            cmd.Parameters.AddWithValue("@OrderType", orderType);
            cmd.Parameters.AddWithValue("@MemberIdNo", input.MemberIdNo);
            cmd.Parameters.AddWithValue("@KitName", (object?)kitName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3) Roll up the detail rows back onto the header (amount/qty/BV/PV/shipping).
        await using (var cmd = new SqlCommand($@"
UPDATE a
SET OrderAmt = b.OrderAmount, OrderItem = b.OrderItem, OrderQty = b.OrderQty, RemainQty = b.OrderQty,
    BV = b.BVV, WalletAmt = b.OrderAmount, PV = b.PVV, Shipping = b.ShippingAmt
FROM TrnOrder a
JOIN (
    SELECT COUNT(*) AS OrderItem, SUM(od.Qty) AS OrderQty, SUM(od.NetAmount) AS OrderAmount,
           SUM(pom.BV * od.Qty) AS BVV, SUM(pom.PV * od.Qty) AS PVV, SUM(od.Qty) AS ShippingAmt
    FROM TrnOrderDetail od
    JOIN {invDb}..M_ProductMaster pom ON od.ProductID = pom.ProdID
    WHERE od.OrderNo = @OrderNo AND od.Formno = @FormNo
) b ON 1 = 1
WHERE a.OrderNo = @OrderNo AND a.Formno = @FormNo;", conn, tx))
        {
            cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
            cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
            await cmd.ExecuteNonQueryAsync();
        }

        // 4) Audit trail.
        await using (var cmd = new SqlCommand(
            "INSERT INTO UserHistory (UserId, UserName, PageName, Activity, ModifiedFlds, RecTimeStamp, Memberid) " +
            "VALUES (@FormNo, @IdNo, 'Product Request', 'Product Request', @Modified, GETDATE(), @FormNoInt)",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
            cmd.Parameters.AddWithValue("@IdNo", input.MemberIdNo);
            cmd.Parameters.AddWithValue("@Modified", $"Product Request For Order No {input.OrderNo}");
            cmd.Parameters.AddWithValue("@FormNoInt", input.FormNo);
            await cmd.ExecuteNonQueryAsync();
        }

        // 5) Payment confirmation record lives in the linked inventory DB.
        await using (var cmd = new SqlCommand($@"
INSERT INTO {invDb}..TrnPaymentConfirmation
    (SNo, ConfirmBy, OrderNo, FormNo, OrderAmt, IsConfirm, RecTimeStamp, UserID, OrderFor, IDNO, ActiveStatus, OrdType, FSessId)
SELECT CASE WHEN MAX(SNo) IS NULL THEN 1001 ELSE MAX(SNo) + 1 END, '', @OrderNo, @FormNo,
       @Amount, 'Y', GETDATE(), 0, '', @IdNo, 'Y', 'D', 1
FROM {invDb}..TrnPaymentConfirmation;", conn, tx))
        {
            cmd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
            cmd.Parameters.AddWithValue("@FormNo", input.FormNo);
            cmd.Parameters.AddWithValue("@Amount", input.Amount);
            cmd.Parameters.AddWithValue("@IdNo", input.MemberIdNo);
            await cmd.ExecuteNonQueryAsync();
        }

        // 6) Same Repurchincome + activation logic as the cash path, but BV/PV
        //    now come from the freshly-built TrnOrder header row.
        decimal existingPvValue = await GetExistingPvValueAsync(conn, tx, input.FormNo);
        var (bv, pv) = await GetTrnOrderBvPvAsync(conn, tx, input.OrderNo);

        if (productStatus == "N")
        {
            await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                repurchincome: bv, billType: "A", soldBy: input.PartyCode, pvValue: pv, fromId: input.FormNo);
            await MaybeActivateMemberAsync(conn, tx, input.MemberIdNo, input.OrderNo, existingPvValue + totalPv, bv);
        }
        else if (productStatus == "Y")
        {
            if (productKitId == "5")
            {
                await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                    repurchincome: 0, billType: "A", soldBy: input.PartyCode, pvValue: pv, fromId: null);
                await MaybeActivateMemberAsync(conn, tx, input.MemberIdNo, input.OrderNo,
                    existingPvValue + totalPv, bv, onlyThresholdBranches: true);
            }
            else
            {
                await InsertRepurchincomeAsync(conn, tx, sessid, input.FormNo, input.OrderNo,
                    repurchincome: bv, billType: "R", soldBy: input.PartyCode, pvValue: pv, fromId: input.FormNo);
            }
        }

        await using (var upd = new SqlCommand(
            "UPDATE TrnProductorderDetail SET IsApprove = 'Y', Remark = @Remark, " +
            "ApproveRemark = @ApproveRemark, Approvedate = GETDATE() WHERE OrderNo = @OrderNo",
            conn, tx))
        {
            upd.Parameters.AddWithValue("@Remark", remark);
            upd.Parameters.AddWithValue("@ApproveRemark", input.ApproveRemark ?? string.Empty);
            upd.Parameters.AddWithValue("@OrderNo", input.OrderNo);
            await upd.ExecuteNonQueryAsync();
        }
    }
    private static async Task<decimal> GetExistingPvValueAsync(SqlConnection conn, SqlTransaction tx, string formNo)
    {
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(SUM(PVValue), 0) FROM Repurchincome WHERE FormNo = @FormNo", conn, tx);
        cmd.Parameters.AddWithValue("@FormNo", formNo);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync() ?? 0m);
    }
    private static async Task<(decimal bv, decimal pv)> GetOrderBvPvAsync(SqlConnection conn, SqlTransaction tx, string orderNo)
    {
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(SUM(BV), 0) AS BV, ISNULL(SUM(PV), 0) AS PV FROM TrnProductorderDetail " +
            "WHERE OrderNo = @OrderNo AND IsApprove = 'N'", conn, tx);
        cmd.Parameters.AddWithValue("@OrderNo", orderNo);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            return (Convert.ToDecimal(rdr["BV"]), Convert.ToDecimal(rdr["PV"]));
        }
        return (0m, 0m);
    }
    private static async Task<(decimal bv, decimal pv)> GetTrnOrderBvPvAsync(SqlConnection conn, SqlTransaction tx, string orderNo)
    {
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(BV, 0) AS BV, ISNULL(PV, 0) AS PV FROM TrnOrder WHERE OrderNo = @OrderNo", conn, tx);
        cmd.Parameters.AddWithValue("@OrderNo", orderNo);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            return (Convert.ToDecimal(rdr["BV"]), Convert.ToDecimal(rdr["PV"]));
        }
        return (0m, 0m);
    }
    private static async Task InsertRepurchincomeAsync(SqlConnection conn, SqlTransaction tx, long sessid, string formNo, string orderNo,decimal repurchincome, string billType, string soldBy, decimal pvValue, string? fromId)
    {
        var sql = fromId is null
            ? "INSERT INTO Repurchincome (Sessid, Formno, BillNo, Billdate, Repurchincome, Imported, BillType, " +
              "SoldBy, MSessid, KitId, Dsessid, Remarks, PVvalue, BType) " +
              "SELECT @Sessid, @FormNo, @BillNo, GETDATE(), @Amt, 'N', 'A', @SoldBy, " +
              "ISNULL(MAX(Sessid), 1), 0, CONVERT(varchar, GETDATE(), 112), '', @Pv, 'A' FROM M_MonthSessnMaster"
            : "INSERT INTO Repurchincome (Sessid, Formno, BillNo, Billdate, Repurchincome, Imported, BillType, " +
              "SoldBy, MSessid, KitId, Dsessid, Remarks, PVvalue, BType, FromID) " +
              "SELECT @Sessid, @FormNo, @BillNo, GETDATE(), @Amt, 'N', @BillType, @SoldBy, " +
              "ISNULL(MAX(Sessid), 1), 0, CONVERT(varchar, GETDATE(), 112), '', @Pv, @BillType, @FromId FROM M_MonthSessnMaster";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@Sessid", sessid);
        cmd.Parameters.AddWithValue("@FormNo", formNo);
        cmd.Parameters.AddWithValue("@BillNo", $"Order {orderNo}");
        cmd.Parameters.AddWithValue("@Amt", repurchincome);
        cmd.Parameters.AddWithValue("@SoldBy", soldBy ?? string.Empty);
        cmd.Parameters.AddWithValue("@Pv", pvValue);
        if (fromId is not null)
        {
            cmd.Parameters.AddWithValue("@BillType", billType);
            cmd.Parameters.AddWithValue("@FromId", fromId);
        }
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task MaybeActivateMemberAsync(SqlConnection conn, SqlTransaction tx, string memberIdNo, string orderNo,decimal pvTotal, decimal bv, bool onlyThresholdBranches = false)
    {
        int? level = pvTotal switch
        {
            >= 4950m => 4,
            >= 1975m and < 4950m => 5,
            _ => onlyThresholdBranches ? (int?)null : 4
        };
        if (level is null) return;

        await using var cmd = new SqlCommand(
            "EXEC Sp_ActivateMember_New @IdNo, @OrderNo, @PvValue, @Bv, @Level", conn, tx);
        cmd.Parameters.AddWithValue("@IdNo", memberIdNo);
        cmd.Parameters.AddWithValue("@OrderNo", orderNo);
        cmd.Parameters.AddWithValue("@PvValue", pvTotal);
        cmd.Parameters.AddWithValue("@Bv", bv);
        cmd.Parameters.AddWithValue("@Level", level.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier cannot be empty.");
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"Identifier '{name}' contains invalid character '{c}'.");
        }
        return name;
    }
}
