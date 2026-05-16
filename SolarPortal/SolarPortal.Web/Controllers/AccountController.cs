using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Web.ViewModels;

namespace SolarPortal.Web.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILiveDbAuthBridge _liveDbBridge;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILiveDbAuthBridge liveDbBridge,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _liveDbBridge = liveDbBridge;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");

        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return View(model);

        // ─── LiveDB bridge ────────────────────────────────────────────────
        // If the user typed an IdNo from m_membermaster, the bridge will:
        //   1) verify credentials against the live table
        //   2) auto-create / refresh an Identity user with the same password
        //   3) return the synthetic email we should use to sign in
        // If the bridge returns null, we fall through to the existing Identity
        // path (so demo/legacy accounts still work).
        var bridgedEmail = await _liveDbBridge.TryBridgeUserAsync(model.Email, model.Password);

        ApplicationUser? user;
        Microsoft.AspNetCore.Identity.SignInResult result;

        if (bridgedEmail != null)
        {
            // Bridge already verified credentials against m_membermaster.
            // Sign the user in DIRECTLY (no PasswordSignInAsync) to avoid the
            // Identity ArgumentOutOfRangeException(millisecondsDelay) bug that
            // can be thrown by the anti-timing-attack delay on first login.
            user = await _userManager.FindByEmailAsync(bridgedEmail);
            if (user == null)
            {
                ModelState.AddModelError(string.Empty, "Sign-in failed. Try again.");
                return View(model);
            }
            await _signInManager.SignInAsync(user, isPersistent: model.RememberMe);
            result = Microsoft.AspNetCore.Identity.SignInResult.Success;
        }
        else
        {
            // Fall back to standard Identity flow for non-live-DB accounts.
            user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null || !user.IsActive)
            {
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }
            result = await _signInManager.PasswordSignInAsync(
                model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);
        }

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", model.Email);
            var roles = await _userManager.GetRolesAsync(user);

            // ── USER SITE — only User role allowed ─────────────────────────
            if (!roles.Contains("User"))
            {
                await _signInManager.SignOutAsync();
                ModelState.AddModelError(string.Empty,
                    "This account is not authorised for the user site. " +
                    "Use the Admin or Installer site instead.");
                return View(model);
            }

            return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelUserPanel" });
        }

        if (result.IsLockedOut)
            ModelState.AddModelError(string.Empty, "Account locked. Try after 5 minutes.");
        else
            ModelState.AddModelError(string.Empty, "Invalid email or password.");

        return View(model);
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
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
}
