/* ============================================================================
   photo-source.js  —  "Camera / Gallery" chooser for every photo upload
   ----------------------------------------------------------------------------
   Spec (image point 5, user + INC panel):
       "जहां भी Photo Upload हो रही है वहां Camera / Gallery का Option देना है।"

   How it works — deliberately WITHOUT touching any existing markup:
   Every <input type="file"> that accepts images is intercepted in the CAPTURE
   phase, so it also catches the programmatic .click() that some pages fire from
   a "Upload / Replace" button (e.g. the PM Surya page's .pm-pick buttons).
   A small sheet asks Camera vs Gallery, then the SAME input is re-opened with
   the `capture` attribute set (camera) or removed (gallery).

   Only mobile / touch devices get the sheet. On a desktop `capture` is ignored
   by the browser anyway, so there the input keeps its original behaviour and
   nothing changes for existing users.

   Opt out of a single input with  data-no-photo-source="true".
   ========================================================================== */
(function () {
    'use strict';

    // ── Should this device see the chooser at all? ───────────────────────────
    // Camera capture is a mobile-only capability. A coarse pointer (finger)
    // plus a media-capture-capable UA is the practical test.
    function isMobileLike() {
        try {
            if (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) return true;
        } catch (e) { /* older browsers — fall through to the UA test */ }
        return /Android|iPhone|iPad|iPod|Windows Phone|Mobile/i.test(navigator.userAgent || '');
    }

    var ENABLED = isMobileLike();

    // ── Which inputs qualify? ────────────────────────────────────────────────
    function acceptsImage(el) {
        var a = (el.getAttribute('accept') || '').toLowerCase();
        if (!a) return false;                       // no accept → leave it alone
        return a.indexOf('image/') !== -1 ||
               a.indexOf('.jpg') !== -1 || a.indexOf('.jpeg') !== -1 ||
               a.indexOf('.png') !== -1 || a.indexOf('.webp') !== -1;
    }

    // An input that also takes PDFs gets a third "Files" option so the user can
    // still attach a scanned PDF bill / receipt.
    function acceptsNonImage(el) {
        var a = (el.getAttribute('accept') || '').toLowerCase();
        return a.indexOf('.pdf') !== -1 || a.indexOf('application/') !== -1;
    }

    // ── The chooser sheet ────────────────────────────────────────────────────
    var sheet = null;

    function buildSheet() {
        var wrap = document.createElement('div');
        wrap.id = 'photoSourceSheet';
        wrap.setAttribute('style',
            'display:none;position:fixed;inset:0;z-index:2000;background:rgba(0,0,0,.45);');
        wrap.innerHTML =
            '<div class="ps-card" style="position:absolute;left:0;right:0;bottom:0;background:#fff;' +
            'border-radius:16px 16px 0 0;padding:18px 16px calc(18px + env(safe-area-inset-bottom));' +
            'box-shadow:0 -6px 24px rgba(0,0,0,.2)">' +
            '  <div style="width:40px;height:4px;border-radius:2px;background:#e5e7eb;margin:0 auto 14px"></div>' +
            '  <div style="font-weight:600;font-size:15px;color:#1f2937;margin-bottom:12px" id="psTitle">Photo kahan se lein?</div>' +
            '  <button type="button" data-src="camera"  style="' + btnCss() + '">📷 &nbsp;Camera</button>' +
            '  <button type="button" data-src="gallery" style="' + btnCss() + '">🖼️ &nbsp;Gallery</button>' +
            '  <button type="button" data-src="files"   style="' + btnCss() + '" id="psFiles">📄 &nbsp;File / PDF</button>' +
            '  <button type="button" data-src="cancel"  style="' + btnCss(true) + '">Cancel</button>' +
            '</div>';
        document.body.appendChild(wrap);

        // Tapping the backdrop cancels.
        wrap.addEventListener('click', function (ev) {
            if (ev.target === wrap) close();
        });
        return wrap;
    }

    function btnCss(muted) {
        return 'display:block;width:100%;text-align:left;padding:14px 16px;margin-bottom:8px;' +
               'border:1px solid #e5e7eb;border-radius:10px;font-size:15px;cursor:pointer;' +
               (muted ? 'background:#f9fafb;color:#6b7280;text-align:center;'
                      : 'background:#fff;color:#1f2937;');
    }

    function close() {
        if (sheet) sheet.style.display = 'none';
    }

    /**
     * Re-opens `el` with the right source.
     * `psPass` tells the capture-phase listener below to let this click through
     * instead of re-asking — otherwise we would loop forever.
     */
    function reopen(el, source) {
        if (source === 'camera') el.setAttribute('capture', 'environment');
        else el.removeAttribute('capture');

        el.dataset.psPass = '1';
        close();
        // Fired straight from the sheet button's own click handler, so the
        // browser still counts it as a user gesture and opens the picker.
        el.click();
    }

    function ask(el) {
        if (!sheet) sheet = buildSheet();

        var title = sheet.querySelector('#psTitle');
        if (title) {
            var label = el.getAttribute('data-photo-label') || '';
            title.textContent = label ? (label + ' — kahan se lein?') : 'Photo kahan se lein?';
        }

        // The "File / PDF" row only makes sense when the input takes non-images.
        var filesBtn = sheet.querySelector('#psFiles');
        if (filesBtn) filesBtn.style.display = acceptsNonImage(el) ? 'block' : 'none';

        // Rebind every time so the handlers close over the CURRENT input.
        Array.prototype.forEach.call(sheet.querySelectorAll('button[data-src]'), function (b) {
            var clone = b.cloneNode(true);
            b.parentNode.replaceChild(clone, b);
            clone.addEventListener('click', function () {
                var src = clone.getAttribute('data-src');
                if (src === 'cancel') { close(); return; }
                reopen(el, src);
            });
        });

        sheet.style.display = 'block';
    }

    // ── Capture-phase interceptor ────────────────────────────────────────────
    // Capture phase is what makes this work for hidden inputs driven by a
    // separate button (the PM Surya "Upload / Replace" pattern): the synthetic
    // click on the input still travels down through document first.
    document.addEventListener('click', function (ev) {
        if (!ENABLED) return;

        var el = ev.target;
        if (!el || el.tagName !== 'INPUT' || el.type !== 'file') return;
        if (el.hasAttribute('data-no-photo-source')) return;
        if (!acceptsImage(el)) return;

        // Second pass — this is the click WE fired from reopen(). Let it open.
        if (el.dataset.psPass === '1') {
            delete el.dataset.psPass;
            return;
        }

        ev.preventDefault();
        ev.stopPropagation();
        ask(el);
    }, true);
})();
