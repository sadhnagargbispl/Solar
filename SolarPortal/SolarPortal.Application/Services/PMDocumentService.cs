using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class PMDocumentService : IPMDocumentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public PMDocumentService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<PMDocumentDto?> GetByIdAsync(int id)
    {
        var document = await _unitOfWork.PMDocuments.GetByIdAsync(id);
        return document != null ? _mapper.Map<PMDocumentDto>(document) : null;
    }

    public async Task<IEnumerable<PMDocumentDto>> GetByRequestIdAsync(int requestId)
    {
        var documents = await _unitOfWork.PMDocuments.FindAsync(d => d.SolarRequestId == requestId);
        return _mapper.Map<IEnumerable<PMDocumentDto>>(documents);
    }

    public async Task<PMDocumentDto> UploadDocumentAsync(int solarRequestId, DocumentType documentType, string fileName, string filePath, string? contentType, long fileSize)
    {
        // Upsert per (request, documentType). Spec task 10: when a rejected document
        // is re-uploaded it must REPLACE the prior submission (and go back to Pending
        // for the admin to re-review) instead of piling up a second row — the extra
        // rows were why re-uploaded docs did not show correctly on the admin side.
        var existing = (await _unitOfWork.PMDocuments
                            .FindAsync(d => d.SolarRequestId == solarRequestId && d.DocumentType == documentType))
                       .OrderByDescending(d => d.CreatedAt)
                       .FirstOrDefault();

        PMDocument document;
        if (existing != null)
        {
            existing.FileName    = fileName;
            existing.FilePath    = filePath;
            existing.ContentType = contentType;
            existing.FileSize    = fileSize;
            existing.Status      = ApprovalStatus.Pending; // re-review from scratch
            existing.Remarks     = null;                   // clear prior reject remark
            existing.UpdatedAt   = DateTime.UtcNow;
            _unitOfWork.PMDocuments.Update(existing);
            document = existing;
        }
        else
        {
            document = new PMDocument
            {
                SolarRequestId = solarRequestId,
                DocumentType = documentType,
                FileName = fileName,
                FilePath = filePath,
                ContentType = contentType,
                FileSize = fileSize
            };
            await _unitOfWork.PMDocuments.AddAsync(document);
        }

        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<PMDocumentDto>(document);
    }

    public async Task ApproveDocumentAsync(int id, string? remarks)
    {
        var document = await _unitOfWork.PMDocuments.GetByIdAsync(id);
        if (document == null) throw new ArgumentException("Document not found");

        document.Status = ApprovalStatus.Approved;
        document.Remarks = remarks;
        document.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.PMDocuments.Update(document);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task RejectDocumentAsync(int id, string? remarks)
    {
        var document = await _unitOfWork.PMDocuments.GetByIdAsync(id);
        if (document == null) throw new ArgumentException("Document not found");

        document.Status = ApprovalStatus.Rejected;
        document.Remarks = remarks;
        document.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.PMDocuments.Update(document);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task DeleteDocumentAsync(int id)
    {
        var document = await _unitOfWork.PMDocuments.GetByIdAsync(id);
        if (document == null) throw new ArgumentException("Document not found");

        _unitOfWork.PMDocuments.Remove(document);
        await _unitOfWork.SaveChangesAsync();
    }
}