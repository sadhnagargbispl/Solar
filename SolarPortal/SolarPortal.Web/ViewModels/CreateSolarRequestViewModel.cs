using System.ComponentModel.DataAnnotations;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.ViewModels;

public class CreateSolarRequestViewModel
{
    // Step 1 - Personal
    [Required, MaxLength(100)]
    [Display(Name = "Applicant Full Name")]
    public string ApplicantName { get; set; } = string.Empty;

    [Required, Phone]
    [Display(Name = "Mobile Number")]
    public string MobileNumber { get; set; } = string.Empty;

    [Phone]
    [Display(Name = "Alternate Mobile")]
    public string? AlternateMobile { get; set; }

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Address { get; set; } = string.Empty;

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string State { get; set; } = string.Empty;

    [Required, RegularExpression(@"^\d{6}$", ErrorMessage = "Enter valid 6-digit pin code")]
    [Display(Name = "Pin Code")]
    public string PinCode { get; set; } = string.Empty;

    [RegularExpression(@"^\d{12}$", ErrorMessage = "Aadhar must be 12 digits")]
    [Display(Name = "Aadhar Number")]
    public string? AadharNumber { get; set; }

    [RegularExpression(@"^[A-Z]{5}[0-9]{4}[A-Z]{1}$", ErrorMessage = "Enter valid PAN")]
    [Display(Name = "PAN Number")]
    public string? PANNumber { get; set; }

    // Step 2 - Product
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Domestic;

    [Range(0.1, 100)]
    [Display(Name = "KV Capacity")]
    public decimal KVCapacity { get; set; } = 1.1m;

    [Display(Name = "Selected Plan")]
    public string? SelectedPlan { get; set; }

    [Range(0, double.MaxValue)]
    [Display(Name = "Plan Amount")]
    public decimal PlanAmount { get; set; }

    // GPS Photo
    public IFormFile? GPSPhoto { get; set; }
}