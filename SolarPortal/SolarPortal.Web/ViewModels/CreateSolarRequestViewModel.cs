using System.ComponentModel.DataAnnotations;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Web.ViewModels;

public class CreateSolarRequestViewModel
{
    // === Profile-fed & legacy-data fields ===
    // Per spec (multiple iterations):
    //   "User n blank detail h to sab blank jaani chahiye"
    //   "Aadhaar 12 se jyada bhi ho to le le, email PAN sab valid form mein nahi ho to bhi le le"
    //
    // User cannot edit these on the form (readonly from profile) and legacy data
    // may not match any strict format. So we accept ANYTHING — blank, malformed,
    // wrong length, invalid format. No [Required], no [Phone], no [EmailAddress],
    // no [RegularExpression], no [MaxLength] tight bound.
    //
    // The MaxLength caps below are loose ceilings purely as a DB-column safety
    // guard (so a 10,000-char paste doesn't overflow the column). They are NOT
    // validations the user will ever hit in normal use.

    [MaxLength(200)]
    [Display(Name = "Applicant Full Name")]
    public string ApplicantName { get; set; } = string.Empty;

    [MaxLength(30)]
    [Display(Name = "Mobile Number")]
    public string MobileNumber { get; set; } = string.Empty;

    [MaxLength(30)]
    [Display(Name = "Alternate Mobile")]
    public string? AlternateMobile { get; set; }

    [MaxLength(200)]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(150)]
    public string City { get; set; } = string.Empty;

    [MaxLength(150)]
    public string State { get; set; } = string.Empty;

    // No regex — empty OK, weird values OK, too-long OK (capped at MaxLength only)
    [MaxLength(30)]
    [Display(Name = "Pin Code")]
    public string PinCode { get; set; } = string.Empty;

    // Aadhaar / PAN: UI inputs removed. Hidden inputs round-trip whatever the
    // auto-stub had. Bumped MaxLength so 12+ digit Aadhaar or non-standard PAN
    // can pass through without error.
    [MaxLength(50)]
    [Display(Name = "Aadhar Number")]
    public string? AadharNumber { get; set; }

    [MaxLength(50)]
    [Display(Name = "PAN Number")]
    public string? PANNumber { get; set; }

    // === Light Bill ownership (spec task 3) ===
    // "Self" = bill is in the applicant's own name.
    // "BloodRelation" = bill is in a blood relation's name → proof mandatory.
    [Display(Name = "Light Bill Ownership")]
    public string? LightBillOwnership { get; set; }

    [MaxLength(200)]
    [Display(Name = "Relation Holder Name")]
    public string? LightBillRelationName { get; set; }

    [Display(Name = "Light Bill Proof")]
    public IFormFile? LightBillProof { get; set; }

    // === Activation Type ===
    [Display(Name = "Request Type")]
    public RequestType RequestType { get; set; } = RequestType.WithActivation;

    // === Step 2 — Product ===
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Domestic;

    [Display(Name = "Solar Project")]
    public int? SolarProjectId { get; set; }

    [Range(0, 100)]   // 0 allowed for Already Active mode where capacity is irrelevant
    [Display(Name = "KV Capacity")]
    public decimal KVCapacity { get; set; } = 1.1m;

    [Display(Name = "Selected Plan")]
    public string? SelectedPlan { get; set; }

    [Range(0, double.MaxValue)]
    [Display(Name = "Plan Amount")]
    public decimal PlanAmount { get; set; }

    // Legacy basic-product picker (V#SpProductDetail.ProdId). Populated by the
    // With Activation card UI; controller re-fetches DP server-side from this id.
    [Display(Name = "Basic Product")]
    public int? ExternalProductId { get; set; }

    public IFormFile? GPSPhoto { get; set; }

    // === First Payment (submitted together with the request per spec) ===
    // Payment fields ARE actively validated — these are user-entered values the
    // user CAN fix. Different from the profile fields above.
    [Display(Name = "Payment Amount")]
    [Range(typeof(decimal), "1", "10000000",
        ErrorMessage = "Enter a valid payment amount.")]
    public decimal PaymentAmount { get; set; }

    [Display(Name = "UTR / Transaction No.")]
    [MaxLength(50)]
    public string? PaymentUTR { get; set; }

    [Display(Name = "Payment Date")]
    [DataType(DataType.Date)]
    public DateTime? PaymentDate { get; set; }

    [Display(Name = "Payment Receipt")]
    public IFormFile? PaymentReceipt { get; set; }

    [Display(Name = "Payment Method")]
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }
}
