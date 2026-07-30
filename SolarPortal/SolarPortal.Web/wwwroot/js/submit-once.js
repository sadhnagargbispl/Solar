/*
 * submit-once.js — site-wide double-submit / multi-click protection.
 *
 * Loaded from _Layout.cshtml, so it applies to every page in every area
 * (user panel + installer panel). Two things are covered:
 *
 *   1. Normal <form> POSTs — a second submit while the first is still in
 *      flight is swallowed, and the submit control is visibly parked.
 *   2. AJAX submit buttons — pages that post with fetch() call
 *      window.solarSubmitGuard(btn) to borrow the same lock.
 *
 * Deliberate design notes:
 *
 *   • The submit listener runs in the BUBBLE phase and bails when
 *     e.defaultPrevented is already set. Page-level validation (inline
 *     onsubmit="return false", or a handler on the form itself) therefore runs
 *     FIRST — otherwise a form that failed validation would stay locked
 *     forever and the user could never fix and resubmit.
 *
 *   • Buttons that carry a name= are NOT disabled, only made unclickable.
 *     A disabled control is omitted from the submitted form data, so
 *     disabling a named submitter can change what the server receives.
 *
 *   • pageshow/persisted unlocks everything: the browser restores a cached
 *     page on Back without re-running scripts, which would otherwise leave a
 *     permanently dead button.
 *
 * Opt out on a single form with data-allow-resubmit="true".
 */
(function () {
    'use strict';

    var BUSY = 'solarBusy';          // dataset flag on the form
    var WAIT_LABEL = 'Please wait…';

    function park(el) {
        if (el.dataset.solarParked === '1') return;
        el.dataset.solarParked = '1';

        if (el.tagName === 'BUTTON') {
            el.dataset.solarLabel = el.innerHTML;
            el.innerHTML = WAIT_LABEL;
        }

        // Named submitters must keep taking part in the form data, so they get
        // blocked visually rather than disabled.
        if (el.hasAttribute('name')) {
            el.style.pointerEvents = 'none';
            el.setAttribute('aria-disabled', 'true');
        } else {
            el.disabled = true;
        }
        el.style.opacity = '0.65';
        el.style.cursor = 'not-allowed';
    }

    function release(el) {
        if (el.dataset.solarParked !== '1') return;
        delete el.dataset.solarParked;

        if (el.tagName === 'BUTTON' && typeof el.dataset.solarLabel === 'string') {
            el.innerHTML = el.dataset.solarLabel;
            delete el.dataset.solarLabel;
        }
        el.disabled = false;
        el.style.pointerEvents = '';
        el.removeAttribute('aria-disabled');
        el.style.opacity = '';
        el.style.cursor = '';
    }

    function submittersOf(form) {
        return form.querySelectorAll(
            'button[type="submit"], input[type="submit"], input[type="image"], button:not([type])');
    }

    function lockForm(form) {
        form.dataset[BUSY] = '1';
        Array.prototype.forEach.call(submittersOf(form), park);
    }

    function unlockForm(form) {
        delete form.dataset[BUSY];
        Array.prototype.forEach.call(submittersOf(form), release);
    }

    // ── 1. Normal form POSTs ──────────────────────────────────────────────
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form || form.nodeName !== 'FORM') return;
        if (form.dataset.allowResubmit === 'true') return;

        // Page validation already stopped this submit — do not lock.
        if (e.defaultPrevented) return;

        if (form.dataset[BUSY] === '1') {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        lockForm(form);
    }, false);

    // ── 2. Shared lock for AJAX submit buttons ────────────────────────────
    // Usage in a page script:
    //     if (!window.solarSubmitGuard(btn)) return;   // already running
    //     try { await fetch(...) } finally { window.solarSubmitGuard.release(btn); }
    // Leave it locked instead of releasing when the page is about to reload.
    window.solarSubmitGuard = function (btn) {
        if (!btn) return true;
        if (btn.dataset.solarParked === '1') return false;
        park(btn);
        return true;
    };
    window.solarSubmitGuard.release = function (btn) { if (btn) release(btn); };

    // ── 3. Back/forward cache restore ─────────────────────────────────────
    window.addEventListener('pageshow', function (e) {
        if (!e.persisted) return;
        Array.prototype.forEach.call(document.querySelectorAll('form'), unlockForm);
        Array.prototype.forEach.call(
            document.querySelectorAll('[data-solar-parked="1"]'), release);
    });
})();
