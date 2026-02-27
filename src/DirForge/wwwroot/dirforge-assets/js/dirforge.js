// === Theme Toggle ===
(function() {
    var toggle = document.getElementById('themeToggle');
    if (!toggle) return;
    toggle.addEventListener('click', function() {
        var current = document.documentElement.getAttribute('data-theme') || 'light';
        var next = current === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', next);
        localStorage.setItem('theme', next);
    });
})();

// === Lightbox ===
(function () {
    'use strict';

    var PRELOAD_AHEAD = 2;
    var IMAGE_CACHE = {};
    var images = [];
    var currentIndex = -1;
    var overlay, imgEl, prevBtn, nextBtn, closeBtn, counter, infoBar, spinner;
    var touchStartX = 0;
    var touchDelta = 0;
    var isDragging = false;

    function injectStyles() {
        var css = [
            '.lb-overlay{position:fixed;inset:0;z-index:70;background:rgba(0,0,0,0.92);display:flex;align-items:center;justify-content:center;flex-direction:column;opacity:0;pointer-events:none;transition:opacity .2s ease}',
            '.lb-overlay.lb-visible{opacity:1;pointer-events:auto}',
            '.lb-overlay.lb-hiding{opacity:0;pointer-events:none}',
            '.lb-img-wrap{position:relative;flex:1;display:flex;align-items:center;justify-content:center;width:100%;min-height:0;padding:48px 56px 0}',
            '.lb-img{max-width:100%;max-height:100%;object-fit:contain;display:block;user-select:none;-webkit-user-select:none;opacity:0;transition:opacity .2s ease}',
            '.lb-img.lb-loaded{opacity:1}',
            '.lb-spinner{position:absolute;width:32px;height:32px;border:3px solid rgba(255,255,255,0.2);border-top-color:#fff;border-radius:50%;animation:lb-spin .7s linear infinite}',
            '@keyframes lb-spin{to{transform:rotate(360deg)}}',
            '.lb-close{position:absolute;top:12px;right:12px;z-index:2;background:none;border:none;color:#fff;cursor:pointer;padding:8px;opacity:.7;transition:opacity .15s}',
            '.lb-close:hover,.lb-close:focus{opacity:1}',
            '.lb-nav{position:absolute;top:50%;transform:translateY(-50%);z-index:2;background:rgba(0,0,0,0.4);border:none;color:#fff;cursor:pointer;padding:12px 10px;border-radius:4px;opacity:.7;transition:opacity .15s}',
            '.lb-nav:hover,.lb-nav:focus{opacity:1}',
            '.lb-nav:disabled{opacity:.2;cursor:default}',
            '.lb-prev{left:8px}',
            '.lb-next{right:8px}',
            '.lb-bottom{display:flex;align-items:center;justify-content:center;gap:16px;padding:10px 16px;color:rgba(255,255,255,0.85);font-size:13px;font-family:var(--font-sans,system-ui,-apple-system,sans-serif);flex-shrink:0;width:100%}',
            '.lb-counter{font-variant-numeric:tabular-nums}',
            '.lb-info{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:50vw}',
            '@media(max-width:600px){.lb-img-wrap{padding:40px 8px 0}.lb-nav{padding:10px 6px}.lb-prev{left:4px}.lb-next{right:4px}.lb-bottom{font-size:12px;gap:10px;padding:8px 12px}.lb-info{max-width:60vw}}',
            '@media print{.lb-overlay{display:none!important}}',
            '@media(prefers-reduced-motion:reduce){.lb-overlay,.lb-img{transition:none}.lb-spinner{animation:none}}'
        ].join('\n');
        var style = document.createElement('style');
        style.textContent = css;
        document.head.appendChild(style);
    }

    function buildDOM() {
        overlay = document.createElement('div');
        overlay.className = 'lb-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Image lightbox');

        var wrap = document.createElement('div');
        wrap.className = 'lb-img-wrap';

        spinner = document.createElement('div');
        spinner.className = 'lb-spinner';
        wrap.appendChild(spinner);

        imgEl = document.createElement('img');
        imgEl.className = 'lb-img';
        imgEl.alt = '';
        imgEl.draggable = false;
        wrap.appendChild(imgEl);

        closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'lb-close';
        closeBtn.title = 'Close (Esc)';
        closeBtn.setAttribute('aria-label', 'Close lightbox');
        closeBtn.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';

        prevBtn = document.createElement('button');
        prevBtn.type = 'button';
        prevBtn.className = 'lb-nav lb-prev';
        prevBtn.title = 'Previous image';
        prevBtn.setAttribute('aria-label', 'Previous image');
        prevBtn.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="15 18 9 12 15 6"/></svg>';

        nextBtn = document.createElement('button');
        nextBtn.type = 'button';
        nextBtn.className = 'lb-nav lb-next';
        nextBtn.title = 'Next image';
        nextBtn.setAttribute('aria-label', 'Next image');
        nextBtn.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 18 15 12 9 6"/></svg>';

        var bottom = document.createElement('div');
        bottom.className = 'lb-bottom';

        counter = document.createElement('span');
        counter.className = 'lb-counter';

        infoBar = document.createElement('span');
        infoBar.className = 'lb-info';

        bottom.appendChild(counter);
        bottom.appendChild(infoBar);

        overlay.appendChild(closeBtn);
        overlay.appendChild(prevBtn);
        overlay.appendChild(wrap);
        overlay.appendChild(nextBtn);
        overlay.appendChild(bottom);

        document.body.appendChild(overlay);
    }

    function bindEvents() {
        closeBtn.addEventListener('click', close);
        prevBtn.addEventListener('click', function () { navigate(-1); });
        nextBtn.addEventListener('click', function () { navigate(1); });

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay || e.target.classList.contains('lb-img-wrap')) {
                close();
            }
        });

        document.addEventListener('keydown', function (e) {
            if (e.defaultPrevented || currentIndex < 0) return;
            if (e.key === 'Escape') { close(); e.preventDefault(); }
            else if (e.key === 'ArrowLeft') { navigate(-1); e.preventDefault(); }
            else if (e.key === 'ArrowRight') { navigate(1); e.preventDefault(); }
        });

        // Touch swipe
        var imgWrap = overlay.querySelector('.lb-img-wrap');
        imgWrap.addEventListener('touchstart', function (e) {
            if (e.touches.length === 1) {
                touchStartX = e.touches[0].clientX;
                touchDelta = 0;
                isDragging = true;
            }
        }, { passive: true });

        imgWrap.addEventListener('touchmove', function (e) {
            if (isDragging && e.touches.length === 1) {
                touchDelta = e.touches[0].clientX - touchStartX;
            }
        }, { passive: true });

        imgWrap.addEventListener('touchend', function () {
            if (!isDragging) return;
            isDragging = false;
            if (Math.abs(touchDelta) > 50) {
                navigate(touchDelta < 0 ? 1 : -1);
            }
            touchDelta = 0;
        });
    }

    function preloadImage(url) {
        if (IMAGE_CACHE[url]) return;
        var img = new Image();
        img.src = url;
        IMAGE_CACHE[url] = img;
    }

    function preloadAround(index) {
        for (var i = Math.max(0, index - PRELOAD_AHEAD); i <= Math.min(images.length - 1, index + PRELOAD_AHEAD); i++) {
            preloadImage(images[i].url);
        }
    }

    function showImage(index) {
        var img = images[index];
        currentIndex = index;

        imgEl.classList.remove('lb-loaded');
        spinner.style.display = 'block';

        counter.textContent = (index + 1) + ' / ' + images.length;
        infoBar.textContent = img.name + (img.size ? ' \u2014 ' + img.size : '');
        infoBar.title = img.name;

        prevBtn.disabled = index === 0;
        nextBtn.disabled = index === images.length - 1;

        var onLoad = function () {
            imgEl.removeEventListener('load', onLoad);
            imgEl.removeEventListener('error', onError);
            spinner.style.display = 'none';
            imgEl.classList.add('lb-loaded');
        };

        var onError = function () {
            imgEl.removeEventListener('load', onLoad);
            imgEl.removeEventListener('error', onError);
            spinner.style.display = 'none';
            imgEl.alt = 'Failed to load image';
            imgEl.classList.add('lb-loaded');
        };

        imgEl.addEventListener('load', onLoad);
        imgEl.addEventListener('error', onError);
        imgEl.src = img.url;
        imgEl.alt = img.name;

        // If already cached and complete
        if (imgEl.complete && imgEl.naturalWidth > 0) {
            imgEl.removeEventListener('load', onLoad);
            imgEl.removeEventListener('error', onError);
            spinner.style.display = 'none';
            imgEl.classList.add('lb-loaded');
        }

        preloadAround(index);
    }

    function navigate(dir) {
        var next = currentIndex + dir;
        if (next < 0 || next >= images.length) return;
        showImage(next);
    }

    function open(startIndex) {
        if (!images.length) return;
        var idx = (typeof startIndex === 'number' && startIndex >= 0 && startIndex < images.length) ? startIndex : 0;
        overlay.classList.add('lb-visible');
        overlay.classList.remove('lb-hiding');
        document.body.style.overflow = 'hidden';
        showImage(idx);
    }

    function close() {
        if (currentIndex < 0) return;
        overlay.classList.remove('lb-visible');
        overlay.classList.add('lb-hiding');
        currentIndex = -1;
        document.body.style.overflow = '';
        setTimeout(function () {
            overlay.classList.remove('lb-hiding');
            imgEl.src = '';
            imgEl.alt = '';
        }, 200);
    }

    function init() {
        var dataEl = document.getElementById('lightboxImageData');
        if (!dataEl) return;

        try {
            images = JSON.parse(dataEl.textContent) || [];
        } catch (e) {
            images = [];
        }

        if (!images.length) return;

        injectStyles();
        buildDOM();
        bindEvents();

        var trigger = document.getElementById('lightboxTrigger');
        if (trigger) {
            trigger.style.display = '';
            trigger.addEventListener('click', function () { open(0); });
        }

        // Bind per-image lightbox buttons
        var lbBtns = document.querySelectorAll('.lightbox-open-btn');
        lbBtns.forEach(function (btn) {
            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var name = btn.getAttribute('data-lightbox-name');
                for (var i = 0; i < images.length; i++) {
                    if (images[i].name === name) {
                        open(i);
                        return;
                    }
                }
            });
        });
    }

    // Public API
    window.DirForgeLightbox = {
        open: function (index) { open(index); },
        close: function () { close(); }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

// === QR Code UI ===
/**
 * DirForge QR Code — modal + inline render API.
 * Uses qrcode-generator by Kazuhiko Arase (MIT) for QR encoding.
 */
(function () {
    'use strict';

    // ── Canvas rendering ───────────────────────────────────────────────────
    function renderToCanvas(url, canvas, cellSize, quietZone) {
        cellSize = cellSize || 8;
        quietZone = quietZone || 4;

        // Auto-detect version (typeNumber = 0) with ECC level M
        var qr = qrcode(0, 'M');
        qr.addData(url);
        qr.make();

        var moduleCount = qr.getModuleCount();
        var totalSize = (moduleCount + quietZone * 2) * cellSize;
        canvas.width = totalSize;
        canvas.height = totalSize;

        var ctx = canvas.getContext('2d');
        ctx.fillStyle = '#ffffff';
        ctx.fillRect(0, 0, totalSize, totalSize);
        ctx.fillStyle = '#000000';

        for (var r = 0; r < moduleCount; r++) {
            for (var c = 0; c < moduleCount; c++) {
                if (qr.isDark(r, c)) {
                    ctx.fillRect(
                        (c + quietZone) * cellSize,
                        (r + quietZone) * cellSize,
                        cellSize, cellSize
                    );
                }
            }
        }
    }

    // ── Modal DOM & CSS ────────────────────────────────────────────────────
    var modalEl = null;
    var modalOpen = false;

    function injectCSS() {
        if (document.getElementById('dirforge-qr-css')) return;
        var style = document.createElement('style');
        style.id = 'dirforge-qr-css';
        style.textContent = [
            '.qr-modal{position:fixed;inset:0;background:rgba(0,0,0,0.5);backdrop-filter:blur(4px);-webkit-backdrop-filter:blur(4px);display:flex;align-items:center;justify-content:center;z-index:55;padding:16px;animation:fadeIn 150ms ease}',
            '[data-theme="dark"] .qr-modal{background:rgba(0,0,0,0.7)}',
            '.qr-modal[hidden]{display:none}',
            '.qr-dialog{width:100%;max-width:360px;background:var(--color-surface);border:1px solid var(--color-border);border-radius:var(--radius-md);box-shadow:var(--shadow-overlay);padding:24px;animation:modalSlideIn 200ms ease;text-align:center}',
            '.qr-dialog-title{margin:0 0 16px 0;font-size:16px;font-weight:600;color:var(--color-text);display:flex;align-items:center;justify-content:space-between}',
            '.qr-close-btn{display:flex;align-items:center;justify-content:center;width:32px;height:32px;background:none;border:1px solid var(--color-border);border-radius:6px;cursor:pointer;color:var(--color-text-secondary);transition:color 120ms ease,border-color 120ms ease,background-color 120ms ease}',
            '.qr-close-btn:hover{color:var(--color-text);border-color:var(--color-text-secondary);background-color:var(--color-surface-hover)}',
            '.qr-canvas-wrap{display:flex;justify-content:center;margin-bottom:16px}',
            '.qr-canvas-wrap canvas{border-radius:8px;max-width:100%;height:auto}',
            '.qr-url{font-family:var(--font-mono);font-size:11px;color:var(--color-text-secondary);word-break:break-all;margin-bottom:12px;max-height:40px;overflow:hidden;text-overflow:ellipsis;line-height:1.4}',
            '.qr-copy-btn{display:inline-flex;align-items:center;gap:6px;border:1px solid var(--color-border);border-radius:6px;background:var(--color-surface);color:var(--color-text-secondary);padding:6px 14px;font-family:var(--font-body);font-size:13px;font-weight:500;cursor:pointer;transition:color 120ms ease,border-color 120ms ease,background-color 120ms ease}',
            '.qr-copy-btn:hover{color:var(--color-primary);border-color:var(--color-primary)}',
            '.qr-copy-btn.copied{color:var(--color-success);border-color:var(--color-success)}',
            '.share-qr-container{text-align:center;padding:12px 0}',
            '.share-qr-container canvas{border-radius:6px;max-width:100%;height:auto}',
            '@media screen and (max-width:480px){.qr-modal{padding:8px;align-items:flex-end}.qr-dialog{border-radius:12px 12px 0 0;max-width:none}}'
        ].join('\n');
        document.head.appendChild(style);
    }

    function ensureModal() {
        if (modalEl) return modalEl;
        injectCSS();
        modalEl = document.createElement('div');
        modalEl.className = 'qr-modal';
        modalEl.hidden = true;
        modalEl.innerHTML = [
            '<div class="qr-dialog" role="dialog" aria-modal="true">',
            '  <div class="qr-dialog-title">',
            '    <span>QR Code</span>',
            '    <button type="button" class="qr-close-btn" title="Close (Esc)" aria-label="Close">',
            '      <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
            '    </button>',
            '  </div>',
            '  <div class="qr-canvas-wrap"><canvas id="qrModalCanvas"></canvas></div>',
            '  <div class="qr-url" id="qrModalUrl"></div>',
            '  <button type="button" class="qr-copy-btn" id="qrModalCopy">',
            '    <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
            '    Copy URL',
            '  </button>',
            '</div>'
        ].join('');

        document.body.appendChild(modalEl);

        // Close button
        modalEl.querySelector('.qr-close-btn').addEventListener('click', closeModal);

        // Backdrop click
        modalEl.addEventListener('click', function (e) {
            if (e.target === modalEl) closeModal();
        });

        // Copy button
        var copyBtn = modalEl.querySelector('#qrModalCopy');
        var copySvg = copyBtn.innerHTML;
        var checkSvg = '<svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg> Copied!';
        copyBtn.addEventListener('click', function () {
            var url = modalEl.querySelector('#qrModalUrl').textContent;
            if (!url) return;
            var done = function () {
                copyBtn.innerHTML = checkSvg;
                copyBtn.classList.add('copied');
                setTimeout(function () {
                    copyBtn.innerHTML = copySvg;
                    copyBtn.classList.remove('copied');
                }, 2000);
            };
            if (navigator.clipboard && window.isSecureContext) {
                navigator.clipboard.writeText(url).then(done);
            } else {
                var ta = document.createElement('textarea');
                ta.value = url;
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.select();
                document.execCommand('copy');
                document.body.removeChild(ta);
                done();
            }
        });

        return modalEl;
    }

    function showModal(url) {
        var el = ensureModal();
        var canvas = el.querySelector('#qrModalCanvas');

        try {
            renderToCanvas(url, canvas, 6, 4);
        } catch (e) {
            return; // URL too long or encoding error
        }

        el.querySelector('#qrModalUrl').textContent = url;
        el.hidden = false;
        modalOpen = true;
    }

    function closeModal() {
        if (modalEl) {
            modalEl.hidden = true;
            modalOpen = false;
        }
    }

    function renderInline(url, container) {
        injectCSS();
        container.innerHTML = '';
        var canvas = document.createElement('canvas');

        try {
            renderToCanvas(url, canvas, 5, 4);
        } catch (e) {
            container.textContent = 'URL too long for QR code';
            return;
        }

        container.appendChild(canvas);
    }

    // ── Public API ─────────────────────────────────────────────────────────
    window.DirForgeQR = {
        show: showModal,
        close: closeModal,
        isOpen: function () { return modalOpen; },
        renderToContainer: renderInline
    };
})();

// === Archive Preview ===
(function() {
    var previewModal = document.getElementById('previewModal');
    if (!previewModal || !previewModal.classList.contains('preview-modal')) return;
    // Only run on ArchiveBrowse pages (detected by data-preview-url attribute on buttons)
    var firstBtn = document.querySelector('.preview-open-btn[data-preview-url]');
    if (!firstBtn) return;

    var previewContent = document.getElementById('previewContent');
    var previewMeta = document.getElementById('previewMeta');
    var previewTitle = document.getElementById('previewTitle');
    var previewIcon = document.getElementById('previewIcon');
    var previewCloseBtn = document.getElementById('previewClose');
    var previewPrev = document.getElementById('previewPrev');
    var previewNext = document.getElementById('previewNext');
    var previewPosition = document.getElementById('previewPosition');
    var previewDownload = document.getElementById('previewDownload');
    var previewCurrentIndex = -1;

    var previewFiles = [];
    document.querySelectorAll('.preview-open-btn[data-preview-url]').forEach(function(btn, i) {
        previewFiles.push({
            infoUrl: btn.getAttribute('data-preview-url'),
            icon: btn.getAttribute('data-preview-icon')
        });
        btn.addEventListener('click', function() {
            openPreview(i);
        });
    });

    function escapeHtml(text) {
        var el = document.createElement('span');
        el.textContent = text;
        return el.innerHTML;
    }

    function openPreview(index) {
        if (!previewModal || index < 0 || index >= previewFiles.length) {
            return;
        }

        previewCurrentIndex = index;
        previewModal.hidden = false;
        document.body.style.overflow = 'hidden';
        loadPreview(index);
    }

    function closePreview() {
        if (!previewModal) {
            return;
        }

        previewModal.hidden = true;
        document.body.style.overflow = '';
    }

    function loadPreview(index) {
        var file = previewFiles[index];
        if (!file || !file.infoUrl) {
            previewContent.innerHTML = '<div class="preview-none"><p>Preview is unavailable.</p></div>';
            previewMeta.innerHTML = '';
            return;
        }

        previewContent.innerHTML = '<div class="preview-loading">Loading\u2026</div>';
        previewMeta.innerHTML = '';
        previewTitle.textContent = '';
        previewIcon.src = file.icon || '';
        previewPosition.textContent = (index + 1) + ' / ' + previewFiles.length;
        previewPrev.disabled = index === 0;
        previewNext.disabled = index === previewFiles.length - 1;

        fetch(file.infoUrl)
            .then(function(r) {
                if (!r.ok) throw new Error('Failed to load preview (' + r.status + ')');
                return r.json();
            })
            .then(function(data) {
                previewTitle.textContent = data.name || '';
                if (previewDownload) previewDownload.href = data.downloadUrl || '#';
                renderPreviewContent(data, file.infoUrl);
                renderPreviewMeta(data);
            })
            .catch(function(err) {
                previewContent.innerHTML = '<div class="preview-none"><p>' + escapeHtml(err.message) + '</p></div>';
                previewMeta.innerHTML = '';
            });
    }

    function renderPreviewContent(data, fetchUrl) {
        if (data.previewMode === 'text') {
            var html = '<div class="preview-text-wrap"><pre>' + escapeHtml(data.textContent || '') + '</pre>';
            if (data.textTruncated) {
                html += '<div class="preview-truncated">File truncated at 128 KB.</div>';
            }
            html += '</div>';
            previewContent.innerHTML = html;
            return;
        }

        previewContent.innerHTML = '<div class="preview-none"><img src="' + escapeHtml(data.iconPath || '') + '" alt="" class="preview-none-icon"><p>No preview available</p><button type="button" class="preview-text-btn">Show text preview</button></div>';
        var btn = previewContent.querySelector('.preview-text-btn');
        if (btn && fetchUrl) {
            btn.addEventListener('click', function() {
                btn.disabled = true;
                btn.textContent = 'Loading\u2026';
                fetch(fetchUrl + (fetchUrl.indexOf('?') >= 0 ? '&' : '?') + 'forceText=true')
                    .then(function(r) { return r.json(); })
                    .then(function(d) {
                        var h = '<div class="preview-text-wrap"><pre>' + escapeHtml(d.textContent || '') + '</pre>';
                        if (d.textTruncated) {
                            h += '<div class="preview-truncated">File truncated at 128 KB.</div>';
                        }
                        h += '</div>';
                        previewContent.innerHTML = h;
                    })
                    .catch(function() {
                        btn.textContent = 'Failed to load';
                    });
            });
        }
    }

    function renderPreviewMeta(data) {
        var chips = [
            { label: 'MIME', value: data.mimeType || '\u2014' }
        ];
        if (data.detectedFileType) {
            chips.push({ label: 'Detected', value: data.detectedFileType });
        }
        chips.push({ label: 'Size', value: data.humanSize || '\u2014' });
        chips.push({ label: 'Modified', value: data.modified || '\u2014' });

        var html = '';
        for (var i = 0; i < chips.length; i++) {
            html += '<span class="preview-chip"><span class="preview-chip-label">' + escapeHtml(chips[i].label) + '</span>' + escapeHtml(chips[i].value) + '</span>';
        }

        previewMeta.innerHTML = html;
    }

    if (previewCloseBtn) {
        previewCloseBtn.addEventListener('click', closePreview);
    }

    if (previewModal) {
        previewModal.addEventListener('click', function(e) {
            if (e.target === previewModal) {
                closePreview();
            }
        });
    }

    if (previewPrev) {
        previewPrev.addEventListener('click', function() {
            if (previewCurrentIndex > 0) {
                openPreview(previewCurrentIndex - 1);
            }
        });
    }

    if (previewNext) {
        previewNext.addEventListener('click', function() {
            if (previewCurrentIndex < previewFiles.length - 1) {
                openPreview(previewCurrentIndex + 1);
            }
        });
    }

    document.addEventListener('keydown', function(e) {
        if (!previewModal || previewModal.hidden || e.defaultPrevented) {
            return;
        }

        if (e.key === 'Escape') {
            closePreview();
            e.preventDefault();
        } else if (e.key === 'ArrowLeft' && previewCurrentIndex > 0) {
            openPreview(previewCurrentIndex - 1);
            e.preventDefault();
        } else if (e.key === 'ArrowRight' && previewCurrentIndex < previewFiles.length - 1) {
            openPreview(previewCurrentIndex + 1);
            e.preventDefault();
        }
    });
})();

// === Directory Listing ===
(function() {
    // --- View Toggle ---
    var viewToggle = document.getElementById('viewToggle');
    var gridView = document.querySelector('.grid-view');
    var tableView = document.querySelector('table.listing');
    var summaryFiles = document.getElementById('summaryFiles');
    var summaryFolders = document.getElementById('summaryFolders');
    var summaryVisibleTotal = document.getElementById('summaryVisibleTotal');

    function parseSizeBytes(rawValue) {
        if (!rawValue) return 0;
        var parsed = parseInt(rawValue, 10);
        if (isNaN(parsed) || parsed < 0) return 0;
        return parsed;
    }

    function humanizeSize(size) {
        var safeSize = Math.max(0, size || 0);
        if (safeSize < 1024) {
            return safeSize + ' B';
        }

        var units = ['KB', 'MB', 'GB', 'TB'];
        var unitIndex = 0;
        var value = safeSize / 1024;

        while (unitIndex < units.length - 1 && value >= 1024) {
            value = value / 1024;
            unitIndex++;
        }

        if (value >= 100) return value.toFixed(0) + ' ' + units[unitIndex];
        if (value >= 10) return value.toFixed(1) + ' ' + units[unitIndex];
        return value.toFixed(2) + ' ' + units[unitIndex];
    }

    function recomputeListingSummary() {
        if (!summaryFiles || !summaryFolders || !summaryVisibleTotal) {
            return;
        }

        var rows = document.querySelectorAll('tr[data-entry-row="1"]');
        var fileCount = 0;
        var folderCount = 0;
        var fileBytes = 0;
        var knownFolderCount = 0;
        var knownFolderBytes = 0;

        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var isDirectory = row.getAttribute('data-is-directory') === 'true';
            var isSizeKnown = row.getAttribute('data-size-known') === 'true';
            var sizeBytes = parseSizeBytes(row.getAttribute('data-size-bytes'));

            if (isDirectory) {
                folderCount++;
                if (isSizeKnown) {
                    knownFolderCount++;
                    knownFolderBytes += sizeBytes;
                }
            } else {
                fileCount++;
                fileBytes += sizeBytes;
            }
        }

        summaryFiles.textContent = 'Files: ' + fileCount + ' \u00b7 ' + humanizeSize(fileBytes);
        summaryFolders.textContent = knownFolderCount > 0
            ? 'Folders: ' + folderCount + ' \u00b7 ' + humanizeSize(knownFolderBytes)
            : 'Folders: ' + folderCount;
        var totalCount = fileCount + folderCount;

        if (folderCount === 0) {
            summaryVisibleTotal.textContent = 'Visible total: ' + totalCount + ' \u00b7 ' + humanizeSize(fileBytes);
            return;
        }

        if (knownFolderCount === folderCount) {
            summaryVisibleTotal.textContent = 'Visible total: ' + totalCount + ' \u00b7 ' + humanizeSize(fileBytes + knownFolderBytes);
            return;
        }

        if (knownFolderCount > 0) {
            summaryVisibleTotal.textContent = 'Visible total: ' + totalCount + ' \u00b7 ' + humanizeSize(fileBytes) + ' (files only)';
            return;
        }

        summaryVisibleTotal.textContent = 'Visible total: ' + totalCount + ' \u00b7 ' + humanizeSize(fileBytes) + ' (files only)';
    }

    function setView(mode) {
        document.documentElement.setAttribute('data-view', mode);
        localStorage.setItem('dirforge-view', mode);
        if (mode === 'grid') {
            if (tableView) tableView.hidden = true;
            if (gridView) gridView.hidden = false;
        } else {
            if (tableView) tableView.hidden = false;
            if (gridView) gridView.hidden = true;
        }
    }

    // Restore saved view preference
    var savedView = localStorage.getItem('dirforge-view');
    if (savedView === 'grid' && gridView) {
        setView('grid');
    }

    if (viewToggle) {
        viewToggle.addEventListener('click', function() {
            var current = document.documentElement.getAttribute('data-view') || 'list';
            setView(current === 'grid' ? 'list' : 'grid');
        });
    }

    recomputeListingSummary();

    // Grid card click: files open preview modal
    if (gridView) {
        var gridFileCards = gridView.querySelectorAll('.grid-card-file');
        gridFileCards.forEach(function(card) {
            card.addEventListener('click', function(e) {
                if (e.ctrlKey || e.metaKey || e.button === 1) {
                    // Ctrl/Cmd+click or middle-click: open in new tab
                    e.preventDefault();
                    window.open(card.getAttribute('data-view-href'), '_blank');
                    return;
                }
                e.preventDefault();
                var path = card.getAttribute('data-preview-path');
                for (var i = 0; i < previewFiles.length; i++) {
                    if (previewFiles[i].path === path) {
                        openPreview(i);
                        return;
                    }
                }
                // Fallback: open in new tab
                window.open(card.getAttribute('data-view-href'), '_blank');
            });
        });
    }

    var calcBtn = document.getElementById('calcDirSizes');
    if (calcBtn) {
        calcBtn.addEventListener('click', function() {
            calcBtn.disabled = true;
            calcBtn.title = 'Calculating\u2026';
            var sizesUrl = new URL(window.location.href);
            sizesUrl.searchParams.set('handler', 'DirectorySizes');
            fetch(sizesUrl.toString())
                .then(function(r) { return r.json(); })
                .then(function(sizes) {
                    var rows = document.querySelectorAll('tr[data-dir]:not([data-dir=""])');
                    for (var i = 0; i < rows.length; i++) {
                        var name = rows[i].getAttribute('data-dir');
                        if (sizes[name]) {
                            var cell = rows[i].querySelector('.size-cell');
                            if (cell) {
                                cell.textContent = sizes[name].humanSize;
                                cell.title = sizes[name].tooltip;
                            }

                            if (sizes[name].size !== undefined && sizes[name].size !== null) {
                                var updatedSize = parseSizeBytes(String(sizes[name].size));
                                rows[i].setAttribute('data-size-known', 'true');
                                rows[i].setAttribute('data-size-bytes', String(updatedSize));
                            }
                        }
                    }
                    recomputeListingSummary();
                    calcBtn.disabled = false;
                    calcBtn.title = 'Recalculate subdirectory sizes';
                })
                .catch(function() {
                    calcBtn.disabled = false;
                    calcBtn.title = 'Recalculate subdirectory sizes (retry)';
                });
        });
    }

    var dlBtn = document.getElementById('downloadFolder');
    if (dlBtn) {
        dlBtn.addEventListener('click', function() {
            var url = new URL(window.location.href);
            url.searchParams.set('handler', 'DownloadZip');
            window.location.href = url.toString();
        });
    }

    // --- QR Code ---
    var qrBtn = document.getElementById('qrCodeBtn');
    if (qrBtn) {
        qrBtn.addEventListener('click', function() {
            if (window.DirForgeQR) window.DirForgeQR.show(window.location.href);
        });
    }

    var shareModal = document.getElementById('shareModal');
    var shareOutput = document.getElementById('shareOutput');
    var shareDuration = document.getElementById('shareDuration');
    var shareGenerate = document.getElementById('shareGenerate');
    var shareCopy = document.getElementById('shareCopyInline');
    var shareClose = document.getElementById('shareClose');
    var shareError = document.getElementById('shareError');
    var shareOneTime = document.getElementById('shareOneTime');
    var shareOneTimeWarning = document.getElementById('shareOneTimeWarning');
    var shareTargetPath = '';
    var shareTargetType = '';
    var activeShareToken = document.body.getAttribute('data-share-token') || '';

    function showShareError(message) {
        if (!shareError) return;
        shareError.textContent = message;
        shareError.style.display = 'block';
    }

    function clearShareError() {
        if (!shareError) return;
        shareError.textContent = '';
        shareError.style.display = 'none';
    }

    function openShareModal(path, type) {
        if (!shareModal) return;
        shareTargetPath = path || '';
        shareTargetType = type || '';
        if (shareOutput) shareOutput.value = '';
        if (shareOneTime) shareOneTime.checked = false;
        syncOneTimeWarning();
        clearShareError();
        var shareQr = document.getElementById('shareQrContainer');
        if (shareQr) { shareQr.hidden = true; shareQr.innerHTML = ''; }
        shareModal.hidden = false;
    }

    function closeShareModal() {
        if (!shareModal) return;
        shareModal.hidden = true;
    }

    function syncOneTimeWarning() {
        if (!shareOneTimeWarning) return;
        shareOneTimeWarning.hidden = !(shareOneTime && shareOneTime.checked);
    }

    function canGenerateShareLink() {
        if (!shareTargetType) return false;
        if (shareTargetType === 'file' && !shareTargetPath) return false;
        return true;
    }

    function generateShareLink() {
        if (!canGenerateShareLink()) return;
        clearShareError();
        var shareUrl = new URL(window.location.href);
        shareUrl.search = '';
        shareUrl.searchParams.set('handler', 'ShareLink');

        var requestBody = new URLSearchParams();
        requestBody.set('targetPath', shareTargetPath);
        requestBody.set('targetType', shareTargetType);
        requestBody.set('ttl', shareDuration ? shareDuration.value : '24h');
        requestBody.set('oneTime', shareOneTime && shareOneTime.checked ? 'true' : 'false');

        fetch(shareUrl.toString(), {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                'X-DirForge-Share': '1'
            },
            body: requestBody.toString()
        })
            .then(function(r) {
                if (!r.ok) throw new Error('Failed to generate share link (' + r.status + ')');
                return r.json();
            })
            .then(function(data) {
                if (shareOutput) shareOutput.value = data.url ? window.location.origin + data.url : '';
                var shareQr = document.getElementById('shareQrContainer');
                if (shareQr && window.DirForgeQR && data.url) {
                    shareQr.hidden = false;
                    window.DirForgeQR.renderToContainer(window.location.origin + data.url, shareQr);
                }
            })
            .catch(function(err) {
                showShareError(err.message || 'Failed to generate share link.');
            });
    }

    document.querySelectorAll('.share-open-btn').forEach(function(btn) {
        btn.addEventListener('click', function() {
            openShareModal(btn.getAttribute('data-target-path'), btn.getAttribute('data-target-type'));
        });
    });

    if (shareClose) {
        shareClose.addEventListener('click', closeShareModal);
    }

    if (shareModal) {
        shareModal.addEventListener('click', function(e) {
            if (e.target === shareModal) closeShareModal();
        });
    }

    if (shareOneTime) {
        shareOneTime.addEventListener('change', function() {
            syncOneTimeWarning();
            if (shareModal && !shareModal.hidden) {
                generateShareLink();
            }
        });
        syncOneTimeWarning();
    }

    if (shareDuration) {
        shareDuration.addEventListener('change', function() {
            if (shareModal && !shareModal.hidden) {
                generateShareLink();
            }
        });
    }

    if (shareGenerate) {
        shareGenerate.addEventListener('click', generateShareLink);
    }

    // --- Clipboard helpers ---
    function copyToClipboard(text, onSuccess, onError) {
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(onSuccess)
                .catch(function() { if (onError) onError(); });
        } else {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
            onSuccess();
        }
    }

    if (shareCopy) {
        var shareCopySvgOriginal = shareCopy.innerHTML;
        var shareCopyCheckSvg = '<svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>';
        shareCopy.addEventListener('click', function() {
            if (!shareOutput || !shareOutput.value) {
                showShareError('Generate a link first.');
                return;
            }
            var val = shareOutput.value;
            var done = function() {
                clearShareError();
                shareCopy.innerHTML = shareCopyCheckSvg;
                shareCopy.classList.add('copied');
                setTimeout(function() {
                    shareCopy.innerHTML = shareCopySvgOriginal;
                    shareCopy.classList.remove('copied');
                }, 2000);
            };
            copyToClipboard(val, done, function() {
                showShareError('Copy failed. Paste the link manually.');
            });
        });
    }

    // --- Media pause helper ---
    function pauseActiveMedia() {
        var video = previewContent.querySelector('video');
        var audio = previewContent.querySelector('audio');
        if (video) video.pause();
        if (audio) audio.pause();
    }

    // --- Preview Modal ---
    var previewModal = document.getElementById('previewModal');
    var previewContent = document.getElementById('previewContent');
    var previewMeta = document.getElementById('previewMeta');
    var previewHashes = document.getElementById('previewHashes');
    var previewTitle = document.getElementById('previewTitle');
    var previewIcon = document.getElementById('previewIcon');
    var previewCloseBtn = document.getElementById('previewClose');
    var previewPrev = document.getElementById('previewPrev');
    var previewNext = document.getElementById('previewNext');
    var previewPosition = document.getElementById('previewPosition');
    var previewOpenTab = document.getElementById('previewOpenTab');
    var previewDownload = document.getElementById('previewDownload');
    var previewCurrentIndex = -1;
    var previewVerifyTargetPath = null;

    var previewFiles = [];
    document.querySelectorAll('.preview-open-btn').forEach(function(btn, i) {
        previewFiles.push({ path: btn.getAttribute('data-preview-path'), icon: btn.getAttribute('data-preview-icon') });
        btn.addEventListener('click', function() { openPreview(i); });
    });

    function escapeHtml(text) {
        var el = document.createElement('span');
        el.textContent = text;
        return el.innerHTML;
    }

    function openPreview(index) {
        if (!previewModal || index < 0 || index >= previewFiles.length) return;
        previewCurrentIndex = index;
        previewModal.hidden = false;
        document.body.style.overflow = 'hidden';
        loadPreview(index);
    }

    function closePreview() {
        if (!previewModal) return;
        previewModal.hidden = true;
        document.body.style.overflow = '';
        pauseActiveMedia();
    }

    function loadPreview(index) {
        var file = previewFiles[index];
        previewContent.innerHTML = '<div class="preview-loading">Loading\u2026</div>';
        previewMeta.innerHTML = '';
        previewTitle.textContent = '';
        previewIcon.src = file.icon;
        previewPosition.textContent = (index + 1) + ' / ' + previewFiles.length;
        previewPrev.disabled = index === 0;
        previewNext.disabled = index === previewFiles.length - 1;

        var pathSegments = file.path.split('/').filter(Boolean).map(encodeURIComponent).join('/');
        var url = new URL('/' + pathSegments, window.location.origin);
        url.searchParams.set('handler', 'PreviewInfo');
        var shareParam = activeShareToken;
        if (shareParam) url.searchParams.set('s', shareParam);
        var fetchUrl = url.toString();

        fetch(fetchUrl)
            .then(function(r) {
                if (!r.ok) throw new Error('Failed to load preview (' + r.status + ')');
                return r.json();
            })
            .then(function(data) {
                previewTitle.textContent = data.name;
                previewOpenTab.href = data.viewUrl;
                if (previewDownload) previewDownload.href = data.downloadUrl || '#';

                renderPreviewContent(data, fetchUrl);
                renderPreviewMeta(data);
            })
            .catch(function(err) {
                previewContent.innerHTML = '<div class="preview-none"><p>' + escapeHtml(err.message) + '</p></div>';
            });
    }

    function renderPreviewContent(data, fetchUrl) {
        var html = '';
        switch (data.previewMode) {
            case 'text':
                html = '<div class="preview-text-wrap"><pre>' + escapeHtml(data.textContent || '') + '</pre>';
                if (data.textTruncated) {
                    html += '<div class="preview-truncated">File truncated at 128 KB. Open in new tab to see full content.</div>';
                }
                html += '</div>';
                break;
            case 'image':
                html = '<div class="preview-image"><img src="' + escapeHtml(data.viewUrl) + '" alt="' + escapeHtml(data.name) + '"></div>';
                break;
            case 'video':
                html = '<div class="preview-video"><video controls preload="metadata" src="' + escapeHtml(data.viewUrl) + '"></video></div>';
                break;
            case 'audio':
                html = '<div class="preview-audio"><audio controls preload="metadata" src="' + escapeHtml(data.viewUrl) + '"></audio></div>';
                break;
            case 'pdf':
                html = '<div class="preview-pdf"><iframe src="' + escapeHtml(data.viewUrl) + '" title="PDF preview"></iframe></div>';
                break;
            default:
                html = '<div class="preview-none"><img src="' + escapeHtml(data.iconPath || '') + '" alt="" class="preview-none-icon"><p>No preview available</p><button type="button" class="preview-text-btn">Show text preview</button></div>';
                break;
        }
        previewContent.innerHTML = html;

        if (data.previewMode !== 'text' && data.previewMode !== 'image' && data.previewMode !== 'video' && data.previewMode !== 'audio' && data.previewMode !== 'pdf') {
            var btn = previewContent.querySelector('.preview-text-btn');
            if (btn && fetchUrl) {
                btn.addEventListener('click', function() {
                    btn.disabled = true;
                    btn.textContent = 'Loading\u2026';
                    fetch(fetchUrl + (fetchUrl.indexOf('?') >= 0 ? '&' : '?') + 'forceText=true')
                        .then(function(r) { return r.json(); })
                        .then(function(d) {
                            var h = '<div class="preview-text-wrap"><pre>' + escapeHtml(d.textContent || '') + '</pre>';
                            if (d.textTruncated) {
                                h += '<div class="preview-truncated">File truncated at 128 KB.</div>';
                            }
                            h += '</div>';
                            previewContent.innerHTML = h;
                        })
                        .catch(function() {
                            btn.textContent = 'Failed to load';
                        });
                });
            }
        }
    }

    function renderPreviewMeta(data) {
        var chips = [
            { label: 'MIME', value: data.mimeType }
        ];
        if (data.detectedFileType) {
            chips.push({ label: 'Detected', value: data.detectedFileType });
        }
        chips.push({ label: 'Size', value: data.humanSize });
        chips.push({ label: 'Modified', value: data.modified });
        var html = '';
        for (var i = 0; i < chips.length; i++) {
            html += '<span class="preview-chip"><span class="preview-chip-label">' + escapeHtml(chips[i].label) + '</span>' + escapeHtml(chips[i].value) + '</span>';
        }
        if (data.size <= data.maxFileSizeForHashing) {
            html += '<button type="button" class="preview-hash-btn" id="previewHashBtn">Calculate hashes</button>';
        }
        if (data.sidecarAlgorithm) {
            var verifyLabel = data.sidecarTargetPath
                ? 'Verify ' + escapeHtml(data.sidecarTargetPath.split('/').pop())
                : 'Verify ' + escapeHtml(data.sidecarAlgorithm.toUpperCase());
            html += '<button type="button" class="preview-hash-btn preview-verify-btn" id="previewVerifyBtn">'
                  + verifyLabel + '</button>';
        }
        previewVerifyTargetPath = data.sidecarTargetPath || null;
        previewMeta.innerHTML = html;
        previewHashes.innerHTML = '';

        var hashBtn = document.getElementById('previewHashBtn');
        if (hashBtn) {
            hashBtn.addEventListener('click', function() {
                hashBtn.textContent = 'Calculating\u2026';
                hashBtn.disabled = true;

                var pathSegments = previewFiles[previewCurrentIndex].path.split('/').filter(Boolean).map(encodeURIComponent).join('/');
                var hashUrl = new URL('/' + pathSegments, window.location.origin);
                hashUrl.searchParams.set('handler', 'FileHashes');
                var shareParam = activeShareToken;
                if (shareParam) hashUrl.searchParams.set('s', shareParam);

                fetch(hashUrl.toString())
                    .then(function(r) {
                        if (!r.ok) throw new Error('Hash calculation failed (' + r.status + ')');
                        return r.json();
                    })
                    .then(function(hashes) {
                        var hashNames = ['crc32', 'md5', 'sha1', 'sha256', 'sha512'];
                        var hashLabels = { crc32: 'CRC32', md5: 'MD5', sha1: 'SHA1', sha256: 'SHA256', sha512: 'SHA512' };
                        var hhtml = '';
                        for (var j = 0; j < hashNames.length; j++) {
                            var key = hashNames[j];
                            hhtml += '<span class="preview-hash-chip" title="Click to copy" data-hash-value="' + escapeHtml(hashes[key]) + '">'
                                   + '<span class="preview-chip-label">' + hashLabels[key] + '</span>'
                                   + '<span class="preview-hash-value">' + escapeHtml(hashes[key]) + '</span>'
                                   + '<svg class="hash-check" xmlns="http://www.w3.org/2000/svg" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>'
                                   + '</span>';
                        }
                        hashBtn.style.display = 'none';
                        previewHashes.innerHTML = hhtml;

                        previewHashes.querySelectorAll('.preview-hash-chip').forEach(function(chip) {
                            chip.addEventListener('click', function() {
                                var val = chip.getAttribute('data-hash-value');
                                var label = chip.querySelector('.preview-chip-label');
                                var originalLabel = label ? label.textContent : '';
                                var done = function() {
                                    chip.classList.add('copied');
                                    if (label) label.textContent = 'Copied';
                                    setTimeout(function() {
                                        chip.classList.remove('copied');
                                        if (label) label.textContent = originalLabel;
                                    }, 2000);
                                };
                                copyToClipboard(val, done);
                            });
                        });
                    })
                    .catch(function(err) {
                        previewHashes.innerHTML = '<span class="preview-hash-error">' + escapeHtml(err.message) + '</span>';
                    });
            });
        }

        var verifyBtn = document.getElementById('previewVerifyBtn');
        if (verifyBtn) {
            verifyBtn.addEventListener('click', function() {
                verifyBtn.textContent = 'Verifying\u2026';
                verifyBtn.disabled = true;
                verifyBtn.classList.remove('preview-verify-ok', 'preview-verify-fail');

                var targetPath = previewVerifyTargetPath || previewFiles[previewCurrentIndex].path;
                var pathSegments = targetPath.split('/').filter(Boolean).map(encodeURIComponent).join('/');
                var verifyUrl = new URL('/' + pathSegments, window.location.origin);
                verifyUrl.searchParams.set('handler', 'VerifySidecar');
                var shareParam = activeShareToken;
                if (shareParam) verifyUrl.searchParams.set('s', shareParam);

                fetch(verifyUrl.toString())
                    .then(function(r) {
                        if (!r.ok) return r.text().then(function(t) { throw new Error(t || 'Verification failed (' + r.status + ')'); });
                        return r.json();
                    })
                    .then(function(result) {
                        if (result.verified) {
                            verifyBtn.classList.add('preview-verify-ok');
                            verifyBtn.textContent = 'Verified';
                            verifyBtn.title = result.algorithm.toUpperCase() + ': ' + result.computedHash;
                        } else {
                            verifyBtn.classList.add('preview-verify-fail');
                            verifyBtn.textContent = 'Mismatch';
                            verifyBtn.title = 'Expected: ' + result.expectedHash + '\nGot: ' + result.computedHash;
                        }
                    })
                    .catch(function(err) {
                        verifyBtn.classList.add('preview-verify-fail');
                        verifyBtn.textContent = 'Error';
                        verifyBtn.title = err.message;
                    });
            });
        }
    }

    if (previewCloseBtn) {
        previewCloseBtn.addEventListener('click', closePreview);
    }

    if (previewModal) {
        previewModal.addEventListener('click', function(e) {
            if (e.target === previewModal) closePreview();
        });
    }

    if (previewPrev) {
        previewPrev.addEventListener('click', function() {
            if (previewCurrentIndex > 0) {
                pauseActiveMedia();
                openPreview(previewCurrentIndex - 1);
            }
        });
    }

    if (previewNext) {
        previewNext.addEventListener('click', function() {
            if (previewCurrentIndex < previewFiles.length - 1) {
                pauseActiveMedia();
                openPreview(previewCurrentIndex + 1);
            }
        });
    }

    document.addEventListener('keydown', function(e) {
        if (e.defaultPrevented) return;
        if (e.key === 'Escape' && window.DirForgeQR && window.DirForgeQR.isOpen && window.DirForgeQR.isOpen()) {
            window.DirForgeQR.close();
            e.preventDefault();
            return;
        }
        if (previewModal && !previewModal.hidden) {
            if (e.key === 'Escape') { closePreview(); e.preventDefault(); }
            else if (e.key === 'ArrowLeft' && previewCurrentIndex > 0) { pauseActiveMedia(); openPreview(previewCurrentIndex - 1); e.preventDefault(); }
            else if (e.key === 'ArrowRight' && previewCurrentIndex < previewFiles.length - 1) { pauseActiveMedia(); openPreview(previewCurrentIndex + 1); e.preventDefault(); }
        }
    });
})();
