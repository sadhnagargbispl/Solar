using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILiveDbAuthBridge _liveDbBridge;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILiveDbAuthBridge liveDbBridge,
        ApplicationDbContext db,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _liveDbBridge = liveDbBridge;
        _db = db;
        _logger = logger;
    }

    // panel = "user" (default) | "inc"  — the login page shows a tab for each.
    [HttpGet]
    public IActionResult Login(string? returnUrl = null, string? panel = null)
    {
        // Already signed in: Home/Index sends each role to its own area dashboard
        // (a bare "Dashboard" redirect has no area and 404s).
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        ViewData["ReturnUrl"] = returnUrl;
        ViewBag.Panel = string.Equals(panel, "inc", StringComparison.OrdinalIgnoreCase) ? "inc" : "user";
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null, string? panel = null)
    {
        var isInc = string.Equals(panel, "inc", StringComparison.OrdinalIgnoreCase);
        ViewBag.Panel = isInc ? "inc" : "user";

        if (!ModelState.IsValid)
        {
            // The INC panel labels this field "INC / Installer ID", not "Member ID".
            if (isInc && string.IsNullOrWhiteSpace(model.Email) &&
                ModelState.TryGetValue(nameof(model.Email), out var emailState))
            {
                emailState.Errors.Clear();
                ModelState.AddModelError(nameof(model.Email), "The INC / Installer ID field is required.");
            }
            return View(model);
        }

        // ─── INC / Installer login (Workers table, NOT Identity) ──────────
        // INC workers are created by admin in the Workers table with
        // LoginUsername / LoginPassword and Type = INC. We authenticate against
        // that table and issue a cookie with the "Installer" role claim so the
        // installer area's [Authorize(Roles="Installer")] works.
        if (isInc)
        {
            if (await TrySignInWorkerAsync(model.Email, model.Password, model.RememberMe))
                return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelInstaller" });

            ModelState.AddModelError(string.Empty, "Invalid INC login. Check your ID and password.");
            return View(model);
        }

        if (await TrySignInMemberAsync(model.Email, model.Password, model.RememberMe))
            return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelUserPanel" });

        // ─── Fallback to standard Identity flow for legacy demo accounts ──
        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null || !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", model.Email);
            var roles = await _userManager.GetRolesAsync(user);

            // ── Unified login routing (per spec: INC login from user panel) ──
            // Installer / INC users -> Installer panel; regular users -> user panel.
            if (roles.Contains("Installer"))
                return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelInstaller" });
            if (roles.Contains("User"))
                return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelUserPanel" });

            // Admin / SuperAdmin should use the dedicated admin site.
            await _signInManager.SignOutAsync();
            ModelState.AddModelError(string.Empty,
                "This account is not authorised for this site. Please use the Admin site.");
            return View(model);
        }

        if (result.IsLockedOut)
            ModelState.AddModelError(string.Empty, "Account locked. Try after 5 minutes.");
        else
            ModelState.AddModelError(string.Empty, "Invalid email or password.");

        return View(model);
    }

    // Direct login from the member site, same format as the old app:
    // /Account/Directlogin?refs=TG9naW4=&info=<Base64("IDNO;password")>
    // refs is accepted for compatibility but not used. User panel only.
    [HttpGet]
    public async Task<IActionResult> Directlogin(string? refs, string? info)
    {
        try
        {
            var detail = Encoding.UTF8.GetString(Convert.FromBase64String(info ?? string.Empty));
            var sep = detail.IndexOf(';');   // split on the first ';' only — passwords may contain ';'
            if (sep > 0)
            {
                var idNo = detail[..sep].Trim();
                var password = detail[(sep + 1)..];

                // Drop any session already in this browser.
                await _signInManager.SignOutAsync();

                if (await TrySignInMemberAsync(idNo, password, rememberMe: false))
                    return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelUserPanel" });
            }
        }
        catch (FormatException)
        {
            // info was not valid Base64 — fall through to the login page.
        }

        TempData["Error"] = "Direct login failed. Check ID and password.";
        return RedirectToAction("Login");
    }

    // INC / Installer login (Workers table, NOT Identity). INC workers are
    // created by admin in the Workers table with LoginUsername / LoginPassword.
    // We issue a cookie with the "Installer" role claim so the installer
    // area's [Authorize(Roles="Installer")] works.
    private async Task<bool> TrySignInWorkerAsync(string? loginId, string password, bool rememberMe)
    {
        var uname = (loginId ?? string.Empty).Trim();
        // Both JOB and INC workers share this panel and can log in.
        var worker = await _db.Workers.FirstOrDefaultAsync(w =>
            !w.IsDeleted && w.LoginUsername != null && w.LoginUsername == uname);

        if (worker == null || worker.LoginPassword != password)
            return false;

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "worker-" + worker.Id),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(worker.Name) ? uname : worker.Name),
            new Claim(ClaimTypes.Role, "Installer"),
            new Claim("WorkerId", worker.Id.ToString()),
            new Claim("WorkerType", worker.Type.ToString())
        };
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, principal,
            new AuthenticationProperties { IsPersistent = rememberMe });

        _logger.LogInformation("INC worker {User} (id {Id}) logged in.", uname, worker.Id);
        return true;
    }

    // Member login via the LiveDB bridge. The bridge verifies against
    // m_membermaster and returns a loaded ApplicationUser (via raw ADO.NET)
    // or null. We DO NOT call UserManager.FindByEmailAsync because EF Core's
    // model cache can produce stale SQL that fails against the live DB schema.
    private async Task<bool> TrySignInMemberAsync(string idNo, string password, bool rememberMe)
    {
        var user = await _liveDbBridge.TryBridgeUserAsync(idNo, password);
        if (user == null)
            return false;

        // Bridge already verified credentials — sign in DIRECTLY (no PasswordSignInAsync).
        await _signInManager.SignInAsync(user, isPersistent: rememberMe);
        _logger.LogInformation("User {Email} logged in via live DB bridge.", idNo);

        // Auto-create a SolarRequest on first login.
        // The user is registered in m_membermaster, so Registration is
        // marked as already done. CurrentStage is set to ProductSelection
        // so the user picks their solar plan as the next step. Remaining
        // stages (Payment, Site Survey, etc.) are completed manually.
        var hasAnyRequest = await _db.SolarRequests
            .AsNoTracking()
            .AnyAsync(r => r.UserId == user.Id);

        if (!hasAnyRequest)
            await AutoCreateSolarRequestAsync(user.Id, idNo);

        return true;
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        // Normalize PAN to uppercase (regex accepts both cases for user convenience)
        if (!string.IsNullOrWhiteSpace(model.PANNumber))
            model.PANNumber = model.PANNumber.Trim().ToUpperInvariant();

        if (!ModelState.IsValid)
            return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FullName = model.FullName,
            FatherName = model.FatherName,
            MobileNumber = model.MobileNumber,
            Address = model.Address,
            City = model.City,
            State = model.State,
            PinCode = model.PinCode,
            AadharNumber = model.AadharNumber,
            PANNumber = model.PANNumber,
            EmailConfirmed = false, // Require admin approval
            IsActive = false        // Require admin approval per spec
        };

        var result = await _userManager.CreateAsync(user, model.Password);
        if (result.Succeeded)
        {
            await _userManager.AddToRoleAsync(user, "User");
            // Doc uploads at registration can be wired into FileUploadService + DocumentService
            TempData["Success"] = "Registration successful. Please wait for admin approval.";
            return RedirectToAction("Login");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        return View(model);
    }

    // [Authorize] deliberately absent. Signing out a session that is already gone
    // is a harmless no-op, but with the attribute the expired-cookie case bounced
    // to /Account/Login?ReturnUrl=%2FAccount%2FLogout — and the trip back from the
    // login page is a GET, which a POST-only action answers with 405.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Login");
    }

    // Logout also has to survive a plain GET: a bookmark, a browser prefetch, a
    // page cached from before the button became a form, or the ReturnUrl bounce
    // above. Every one of those used to come back as 405 Method Not Allowed on a
    // POST-only action, which reads to the user as "logout is broken".
    //
    // A GET that ends a session can be triggered cross-site (an <img> tag is
    // enough). That is a nuisance — it can only sign someone out, never act as
    // them — and it is the accepted trade for a logout that always works. The
    // button in the layout still POSTs with the antiforgery token.
    [HttpGet]
    [ActionName("Logout")]
    public async Task<IActionResult> LogoutGet()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Login");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null)
        {
            TempData["Success"] = "If the email exists, a reset link has been sent.";
            return RedirectToAction("Login");
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        // In production, send email with reset link
        TempData["Info"] = $"Reset token (dev only): {token}";
        return View("ForgotPasswordConfirmation");
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    // ────────────────────────────────────────────────────────────────────
    // Auto-create SolarRequest on first login.
    // Registration data comes from m_membermaster (already filled), so
    // the new request starts at the ProductSelection stage. The user then
    // continues through Payment, Site Survey, etc. manually.
    // ────────────────────────────────────────────────────────────────────
    private async Task AutoCreateSolarRequestAsync(string userId, string idNo)
    {
        try
        {
            // 1. Fetch member profile from m_membermaster.
            //    Do NOT call .Trim() inside LINQ — EF Core translates it to SQL
            //    TRIM() which fails on older SQL Server. Match on the raw value
            //    using a pre-trimmed local variable.
            var idNoTrimmed = idNo?.Trim() ?? string.Empty;
            var member = await _db.Members
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.IdNo != null && m.IdNo == idNoTrimmed);

            if (member == null)
            {
                _logger.LogWarning("Auto-create skipped — no m_membermaster row for {IdNo}", idNo);
                return;
            }

            // 2. Generate a request number (SCR-001, SCR-002, ...)
            var existingCount = await _db.SolarRequests.CountAsync();
            var requestNumber = $"SCR-{(existingCount + 1):D3}";

            // 3. Build the SolarRequest with Payment stage (skip Registration + ProductSelection)
            var request = new SolarPortal.Domain.Entities.SolarRequest
            {
                RequestNumber  = requestNumber,
                UserId         = userId,
                ApplicantName  = member.FullName,
                MobileNumber   = member.Mobl?.ToString() ?? string.Empty,
                Email          = member.EMail ?? string.Empty,
                Address        = member.FullAddress,
                City           = member.City ?? string.Empty,
                State          = "Rajasthan",  // adjust if you have state mapping
                PinCode        = member.PinCode ?? string.Empty,
                AadharNumber   = member.AadharNo,
                PANNumber      = member.PanNo,
                RequestType    = SolarPortal.Domain.Enums.RequestType.WithActivation,
                ConnectionType = SolarPortal.Domain.Enums.ConnectionType.Domestic,
                KVCapacity     = 0m,
                PlanAmount     = 0m,
                CurrentStage   = SolarPortal.Domain.Enums.ProjectStatus.ProductSelection,   // ← Registration done, user does Product Selection next
                ApprovalStatus = SolarPortal.Domain.Enums.ApprovalStatus.Pending,
                CreatedAt      = DateTime.UtcNow,
                CreatedBy      = userId,
                IsDeleted      = false
            };

            _db.SolarRequests.Add(request);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Auto-created SolarRequest {Num} for user {IdNo} at Payment stage.",
                requestNumber, idNo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-create SolarRequest failed for {IdNo}", idNo);
            // Non-fatal — user is signed in regardless. They can create a request manually.
        }
    }
}
