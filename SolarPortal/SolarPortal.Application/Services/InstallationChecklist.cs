using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

/// <summary>
/// Checks an installation against the fixed INC upload format (IncUploadFormats).
///
/// Spec: "INC panel par ye pura fill hoga tabhi INC commission de sakta hai.
/// Jisme video ho wo video nahi hai to nahi kar sakta." So the SAME rules gate
/// three places: Mark Complete, Re-submit after a reject, and the commission
/// sweep. Per line:
///   • photos (tagged with that line's FormatItemId) between MinPhotos and MaxPhotos
///     — a line with MaxPhotos = 0 takes no photos;
///   • at least VideoCount videos;
///   • a typed detail when RemarkRequired.
/// </summary>
public static class InstallationChecklist
{
    /// <summary>What has been filed for one format line, plus what is still wrong with it.</summary>
    public sealed class LineStatus
    {
        public IncUploadFormat Format { get; init; } = null!;
        public int Photos { get; init; }
        public int Videos { get; init; }
        public string? Remark { get; init; }
        public string? Problem { get; init; }
        public bool IsComplete => Problem == null;
    }

    public static List<LineStatus> Evaluate(
        IEnumerable<IncUploadFormat> formats,
        IEnumerable<InstallationPhoto> photos,
        IEnumerable<InstallationChecklistEntry> entries)
    {
        var photoList = photos.ToList();
        var entryList = entries.ToList();

        return formats.OrderBy(f => f.SrNo).Select(f =>
        {
            var p = photoList.Count(x => x.FormatItemId == f.Id);
            var v = entryList.Count(x => x.FormatItemId == f.Id && x.EntryType == ChecklistEntryType.Video);
            var remark = entryList
                .Where(x => x.FormatItemId == f.Id && x.EntryType == ChecklistEntryType.Remark)
                .OrderByDescending(x => x.Id)
                .Select(x => x.RemarkText)
                .FirstOrDefault();
            return new LineStatus
            {
                Format = f,
                Photos = p,
                Videos = v,
                Remark = remark,
                Problem = ProblemFor(f, p, v, remark)
            };
        }).ToList();
    }

    /// <summary>
    /// Why this line is not complete yet, or null when it is. Used both on saved
    /// data (Evaluate) and on a submission before anything is written.
    /// </summary>
    public static string? ProblemFor(IncUploadFormat f, int photos, int videos, string? remark)
    {
        var issues = new List<string>();
        if (f.MaxPhotos > 0)
        {
            if (photos < f.MinPhotos)
                issues.Add($"at least {f.MinPhotos} photo(s) needed, {photos} uploaded");
            else if (photos > f.MaxPhotos)
                issues.Add($"maximum {f.MaxPhotos} photo(s) allowed, {photos} uploaded");
        }
        if (f.VideoCount > 0 && videos < f.VideoCount)
            issues.Add(f.VideoCount == 1 ? "video is required" : $"{f.VideoCount} videos are required");
        if (f.RemarkRequired && string.IsNullOrWhiteSpace(remark))
            issues.Add("detail is required");

        return issues.Count == 0 ? null : $"{f.SrNo}. {f.Work} — {string.Join(", ", issues)}";
    }

    /// <summary>All problems, in Sr.No. order. Empty = the whole format is filled.</summary>
    public static List<string> Problems(IEnumerable<LineStatus> lines) =>
        lines.Where(l => !l.IsComplete).Select(l => l.Problem!).ToList();
}
