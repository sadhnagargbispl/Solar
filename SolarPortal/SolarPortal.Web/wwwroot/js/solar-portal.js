// ===================================
// SOLAR PORTAL — Main JS
// ===================================

// CSRF token for AJAX
const csrfToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value;

// Global AJAX helper
async function solarAjax(url, method, data, isFormData = false) {
    const headers = { 'X-Requested-With': 'XMLHttpRequest' };
    if (!isFormData) headers['Content-Type'] = 'application/json';
    if (csrfToken) headers['RequestVerificationToken'] = csrfToken;

    const body = isFormData ? data : JSON.stringify(data);
    const response = await fetch(url, { method, headers, body });
    return await response.json();
}

// ===================================
// FILE UPLOAD PREVIEW
// ===================================

function triggerFile(inputId) {
    document.getElementById(inputId)?.click();
}

function previewSingle(inputId, previewId) {
    const input = document.getElementById(inputId);
    const preview = document.getElementById(previewId);
    if (!input || !preview || !input.files[0]) return;

    const file = input.files[0];
    const ext = file.name.split('.').pop().toLowerCase();

    if (['jpg', 'jpeg', 'png', 'gif', 'webp'].includes(ext)) {
        const reader = new FileReader();
        reader.onload = e => {
            preview.innerHTML = `
                <div class="prev-item" style="max-width:150px">
                    <img src="${e.target.result}" alt="preview" style="width:100%;height:90px;object-fit:cover">
                    <div class="prev-name">${file.name}</div>
                    <button class="prev-del" onclick="clearPreview('${inputId}','${previewId}')">✕</button>
                </div>`;
        };
        reader.readAsDataURL(file);
    } else if (ext === 'pdf') {
        preview.innerHTML = `
            <div class="pdf-prev">
                <span class="pdf-icon">📄</span>
                <div class="pdf-info">
                    <div class="pdf-name">${file.name}</div>
                    <div class="pdf-size">${(file.size / 1024).toFixed(1)} KB</div>
                </div>
                <button class="pdf-del" onclick="clearPreview('${inputId}','${previewId}')">✕</button>
            </div>`;
    }
}

function clearPreview(inputId, previewId) {
    const input = document.getElementById(inputId);
    const preview = document.getElementById(previewId);
    if (input) input.value = '';
    if (preview) preview.innerHTML = '';
}

// ===================================
// OCR SIMULATION
// ===================================

const ocrData = {
    aadhar: {
        'Aadhar Number': '4321 XXXX XXXX',
        'Full Name': 'Ramesh Kumar',
        'Date of Birth': '15/08/1985',
        'Address': 'Jaipur, Rajasthan'
    },
    pan: {
        'PAN Number': 'ABCDE1234F',
        'Name': 'RAMESH KUMAR',
        'Father Name': 'SURESH KUMAR',
        'DOB': '15/08/1985'
    }
};

function runOCR(type, ocrBoxId) {
    const box = document.getElementById(ocrBoxId);
    if (!box) return;

    box.classList.remove('hidden');
    box.innerHTML = `<div class="ocr-label">🔍 Running OCR...</div>
        <div class="ocr-spinner"><span class="spin">⟳</span> Extracting text from document...</div>`;

    setTimeout(() => {
        const data = ocrData[type] || {};
        const fields = Object.entries(data).map(([k, v]) =>
            `<div class="ocr-field"><div class="ocr-fk">${k}</div><div class="ocr-fv">${v}</div></div>`
        ).join('');

        box.innerHTML = `
            <div class="ocr-label">✅ OCR Extracted Data</div>
            <div class="ocr-result">${fields}</div>`;

        // Auto-fill form fields
        if (type === 'aadhar' && document.getElementById('aadhar-field')) {
            document.getElementById('aadhar-field').value = data['Aadhar Number']?.replace(/\s/g, '') || '';
        }
        if (type === 'pan' && document.getElementById('pan-field')) {
            document.getElementById('pan-field').value = data['PAN Number'] || '';
        }
    }, 2000);
}

function previewDoc(inputId, prevId, type) {
    previewSingle(inputId, prevId);
    const ocrId = 'ocr-' + type;
    if (document.getElementById(ocrId)) {
        runOCR(type, ocrId);
    }
}

// ===================================
// TABS
// ===================================

function switchTab(prefix, index, tabEl) {
    // Hide all tab content
    document.querySelectorAll(`[id^="${prefix}-"]`).forEach(el => {
        if (/^\d+$/.test(el.id.replace(prefix + '-', ''))) {
            el.classList.remove('active');
        }
    });

    // Deactivate tab buttons
    if (tabEl) {
        tabEl.closest('.tabs')?.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
        tabEl.classList.add('active');
    }

    // Show target content
    const target = document.getElementById(`${prefix}-${index}`);
    if (target) target.classList.add('active');
}

// ===================================
// RADIO CARD SELECTION
// ===================================

function rcSel(el, group) {
    document.querySelectorAll(`.rc[data-group="${group}"]`).forEach(r => r.classList.remove('sel'));
    el.setAttribute('data-group', group);
    // Mark all in same group
    el.closest('.rc-group')?.querySelectorAll('.rc').forEach(r => r.classList.remove('sel'));
    el.classList.add('sel');
}

// ===================================
// SWEET ALERT HELPERS
// ===================================

function confirmAction(msg, callback) {
    Swal.fire({
        title: 'Are you sure?',
        text: msg,
        icon: 'question',
        showCancelButton: true,
        confirmButtonText: 'Yes, proceed',
        cancelButtonText: 'Cancel',
        background: '#1E293B',
        color: '#F1F5F9',
        confirmButtonColor: '#F59E0B',
        cancelButtonColor: '#334155'
    }).then(result => {
        if (result.isConfirmed) callback();
    });
}

function showSuccess(msg) {
    Swal.fire({
        icon: 'success', title: 'Success!', text: msg,
        background: '#1E293B', color: '#F1F5F9', confirmButtonColor: '#F59E0B', timer: 2500
    });
}

function showError(msg) {
    Swal.fire({
        icon: 'error', title: 'Error', text: msg,
        background: '#1E293B', color: '#F1F5F9', confirmButtonColor: '#EF4444'
    });
}

// ===================================
// ADMIN: APPROVE / REJECT
// ===================================

function approveRequest(id) {
    confirmAction('Approve this solar connection request?', async () => {
        const res = await solarAjax(`/Admin/Projects/Approve`, 'POST', { id });
        if (res.success) {
            showSuccess(res.message || 'Request approved!');
            setTimeout(() => location.reload(), 1500);
        } else {
            showError(res.message || 'Failed to approve');
        }
    });
}

function rejectRequest(id) {
    Swal.fire({
        title: 'Reject Request',
        input: 'textarea',
        inputLabel: 'Rejection Reason',
        inputPlaceholder: 'Enter reason...',
        showCancelButton: true,
        confirmButtonText: 'Reject',
        background: '#1E293B',
        color: '#F1F5F9',
        confirmButtonColor: '#EF4444'
    }).then(async result => {
        if (result.isConfirmed && result.value) {
            const res = await solarAjax(`/Admin/Projects/Reject`, 'POST', { id, reason: result.value });
            if (res.success) {
                showSuccess('Request rejected');
                setTimeout(() => location.reload(), 1500);
            } else {
                showError(res.message);
            }
        }
    });
}

// ===================================
// PAYMENT — Generate Receipt
// ===================================

function generateReceipt() {
    const amount = document.getElementById('pay-amount')?.value;
    const utr = document.getElementById('pay-utr')?.value;
    if (!amount || !utr) {
        showError('Please fill amount and UTR number first');
        return;
    }
    const rcptNo = `SCR-${new Date().getFullYear()}-${Math.floor(1000 + Math.random() * 9000)}`;
    showSuccess(`Receipt No. ${rcptNo} generated!`);
    const rcptField = document.getElementById('receipt-number');
    if (rcptField) rcptField.value = rcptNo;
}

// ===================================
// AJAX FILE UPLOAD FOR DOCUMENTS
// ===================================

async function uploadDocument(requestId, docType, inputId, statusId) {
    const input = document.getElementById(inputId);
    const status = document.getElementById(statusId);
    if (!input?.files[0]) return;

    const formData = new FormData();
    formData.append('requestId', requestId);
    formData.append('documentType', docType);
    formData.append('file', input.files[0]);

    if (status) status.innerHTML = '<span class="spin">⟳</span> Uploading...';

    try {
        const response = await fetch('/User/SolarRequest/UploadDocument', {
            method: 'POST',
            headers: { 'RequestVerificationToken': csrfToken },
            body: formData
        });
        const result = await response.json();

        if (result.success) {
            if (status) status.innerHTML = '<span style="color:var(--green)">✅ Uploaded</span>';
        } else {
            if (status) status.innerHTML = `<span style="color:var(--danger)">✕ ${result.message}</span>`;
        }
    } catch {
        if (status) status.innerHTML = '<span style="color:var(--danger)">✕ Upload failed</span>';
    }
}

// ===================================
// PROGRESS BAR
// ===================================

function animateProgressBars() {
    document.querySelectorAll('.prog-bar').forEach(bar => {
        const target = bar.dataset.width || '0';
        bar.style.width = '0';
        setTimeout(() => bar.style.width = target + '%', 100);
    });
}

document.addEventListener('DOMContentLoaded', animateProgressBars);