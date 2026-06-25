// WhisperSubs -- context menu integration
// Adds "Generate Subtitles" to the three-dot menu on item detail pages and cards (admin only).
// Loaded via script injection into Jellyfin's index.html.
(function () {
    'use strict';

    var isAdmin = null;
    var pendingItemId = null;
    var menuObserver = null;

    function checkAdmin() {
        if (isAdmin !== null) return Promise.resolve(isAdmin);
        return ApiClient.getCurrentUser().then(function (user) {
            isAdmin = user && user.Policy && user.Policy.IsAdministrator;
            return isAdmin;
        }).catch(function () { return false; });
    }

    function showToast(message) {
        try { require(['toast'], function (toast) { toast(message); }); }
        catch (e) { console.log('[WhisperSubs] ' + message); }
    }

    function closeDialog(el) {
        var dialog = el.closest('dialog');
        if (dialog && dialog.close) {
            dialog.close();
            return;
        }
        var btn = el.closest('.actionSheet');
        if (btn) {
            var cancel = btn.querySelector('.btnCloseActionSheet');
            if (cancel) cancel.click();
        }
    }

    function generateSubtitles(itemId) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/GenerateAll', { language: 'auto' });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function createMenuItem(itemId) {
        // Match Jellyfin's exact action sheet button structure
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.type = 'button';
        btn.className = 'listItem listItem-button actionSheetMenuItem btnWhisperSubs';
        btn.setAttribute('data-id', 'whispersubs');

        btn.innerHTML =
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
                '<div class="listItemBodyText actionSheetItemText">Generate Subtitles</div>' +
            '</div>';

        btn.addEventListener('click', function () {
            closeDialog(btn);
            showToast('WhisperSubs: Queuing...');
            generateSubtitles(itemId).then(function (response) {
                var data = typeof response === 'string' ? JSON.parse(response) : response;
                var count = data && data.queued != null ? data.queued : (data && data.count) || 1;
                showToast('WhisperSubs: Queued ' + count + ' item(s) for subtitle generation');
            }).catch(function () {
                showToast('WhisperSubs: Failed to queue generation');
            });
        });

        return btn;
    }

    function injectIntoActionSheet(sheet) {
        if (!pendingItemId) return;
        if (sheet.querySelector('.btnWhisperSubs')) return;

        checkAdmin().then(function (admin) {
            if (!admin) return;

            var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
            var cancelDiv = scroller.querySelector('.buttons');
            var menuItem = createMenuItem(pendingItemId);

            if (cancelDiv) {
                scroller.insertBefore(menuItem, cancelDiv);
            } else {
                scroller.appendChild(menuItem);
            }
        });
    }

    function watchForActionSheet() {
        // Disconnect any previous observer
        if (menuObserver) menuObserver.disconnect();

        menuObserver = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var node = added[j];
                    if (node.nodeType !== 1) continue;

                    var sheet = null;
                    if (node.classList && node.classList.contains('actionSheet')) {
                        sheet = node;
                    } else if (node.querySelector) {
                        sheet = node.querySelector('.actionSheet');
                    }

                    if (sheet) {
                        menuObserver.disconnect();
                        menuObserver = null;
                        injectIntoActionSheet(sheet);
                        return;
                    }
                }
            }
        });

        menuObserver.observe(document.body, { childList: true, subtree: true });

        // Auto-disconnect after 3 seconds
        setTimeout(function () {
            if (menuObserver) {
                menuObserver.disconnect();
                menuObserver = null;
            }
        }, 3000);
    }

    // Capture clicks on three-dot menu triggers everywhere
    document.addEventListener('click', function (e) {
        try {
            if (!e.target || e.target.nodeType !== 1) return;
            var trigger = e.target.closest('.btnMoreCommands, [data-action="menu"]');
            if (!trigger) return;

            // Try to get item ID from the nearest card/item element
            var card = trigger.closest('[data-id]');
            if (card) {
                pendingItemId = card.getAttribute('data-id');
            } else {
                // Detail page fallback: extract from URL hash
                var hash = window.location.hash || '';
                var q = hash.indexOf('?');
                if (q !== -1) {
                    var params = new URLSearchParams(hash.substring(q + 1));
                    pendingItemId = params.get('id');
                }
            }

            if (pendingItemId) {
                watchForActionSheet();
            }
        } catch (err) {
            return;
        }
    }, true); // capture phase to run before Jellyfin's handler

    // Inject a visible "Generate Subtitles" button onto the item detail page (issue #94),
    // in addition to the three-dot context-menu item above. Fail-silent: never throw into the host page.
    function injectDetailButton() {
        try {
            var page = document.querySelector('.libraryPage:not(.hide), .itemDetailPage:not(.hide), .detailPage:not(.hide)');
            if (!page) return;

            // Read the item id from the URL hash at this moment into a LOCAL var
            // (deliberately NOT the module-global pendingItemId, which tracks the ⋮ menu target).
            var hash = window.location.hash || '';
            var m = hash.match(/[?&]id=([^&]+)/);
            if (!m) return;
            var itemId = decodeURIComponent(m[1]);

            // Different Jellyfin versions use different button-row classes.
            var row = page.querySelector('.mainDetailButtons, .detailButtons, .itemActionsBottom, .detailButtonsContainer');
            if (!row) return;

            if (row.querySelector('.btnWhisperSubsDetail')) return;

            checkAdmin().then(function (admin) {
                if (!admin) return;
                if (row.querySelector('.btnWhisperSubsDetail')) return; // re-check after async

                var btn = document.createElement('button');
                btn.setAttribute('is', 'emby-button');
                btn.type = 'button';
                btn.className = 'button-flat detailButton emby-button btnWhisperSubsDetail';
                btn.title = 'Generate subtitles';
                btn.innerHTML =
                    '<div class="detailButton-content">' +
                        '<span class="material-icons detailButton-icon subtitles" aria-hidden="true"></span>' +
                        '<span class="detailButton-icon-text">Subtitles</span>' +
                    '</div>';

                btn.addEventListener('click', function (e) {
                    e.preventDefault();
                    if (btn.disabled) return;
                    btn.disabled = true;
                    showToast('WhisperSubs: Queuing...');
                    generateSubtitles(itemId).then(function (response) {
                        var data = typeof response === 'string' ? JSON.parse(response) : response;
                        var n = data && data.queued != null ? data.queued : (data && data.count) || 1;
                        showToast('WhisperSubs: Queued ' + n + ' item(s) for subtitle generation');
                    }).catch(function () {
                        showToast('WhisperSubs: Failed to queue generation');
                    }).then(function () {
                        setTimeout(function () { btn.disabled = false; }, 3000); // debounce double-clicks
                    });
                });

                row.appendChild(btn);
            });
        } catch (err) {
            console.debug('[WhisperSubs] injectDetailButton error', err);
            return;
        }
    }

    // Jellyfin rebuilds the detail DOM on each SPA navigation, so re-run on nav + render.
    var detailInjectTimer = null;
    function scheduleDetailInject() {
        // Cheap early-exit: this fires on every DOM mutation via the body observer, so on non-detail
        // pages (library grids, home, search) do almost nothing. A detail page always carries an item
        // id in the hash; if there's none, skip without touching the timer.
        if ((window.location.hash || '').indexOf('id=') === -1) return;
        if (detailInjectTimer) clearTimeout(detailInjectTimer);
        detailInjectTimer = setTimeout(injectDetailButton, 150);
    }
    window.addEventListener('hashchange', scheduleDetailInject);
    window.addEventListener('popstate', scheduleDetailInject);
    var detailObserver = new MutationObserver(scheduleDetailInject);
    detailObserver.observe(document.body, { childList: true, subtree: true });
    scheduleDetailInject(); // initial attempt

    console.debug('[WhisperSubs] Context menu integration loaded');
})();
