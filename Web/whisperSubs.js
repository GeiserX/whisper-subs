// WhisperSubs -- context menu integration
// Admins get "Generate Subtitles"; non-admins get "Request Subtitles" when the admin has enabled user
// requests (issue #112). On the item detail page both also get a "Translate into…" list: an admin queues
// the translation directly, a viewer submits it as a request. Loaded via script injection into
// Jellyfin's index.html. This script is served anonymously and is trusted for NOTHING — every check (who
// you are, what you can see, quota, which languages exist) is enforced server-side; the UI here only
// decides which controls to show. Every server string reaches the page through textContent.
(function () {
    'use strict';

    var isAdmin = null;
    var caps = null;          // { enabled, autoApprove } from Requests/Capabilities (non-admins only)
    var translationTargets = null;   // [{ code, name }] from TranslationTargets, cached
    var pendingItemId = null;
    var menuObserver = null;

    function checkAdmin() {
        if (isAdmin !== null) return Promise.resolve(isAdmin);
        return ApiClient.getCurrentUser().then(function (user) {
            isAdmin = user && user.Policy && user.Policy.IsAdministrator;
            return isAdmin;
        }).catch(function () { return false; });
    }

    function getCapabilities() {
        if (caps !== null) return Promise.resolve(caps);
        try {
            var url = ApiClient.getUrl('Plugins/WhisperSubs/Requests/Capabilities');
            return ApiClient.ajax({ type: 'GET', url: url }).then(function (resp) {
                caps = typeof resp === 'string' ? JSON.parse(resp) : resp;
                return caps;
            }).catch(function () { caps = { enabled: false }; return caps; });
        } catch (e) {
            caps = { enabled: false };
            return Promise.resolve(caps);
        }
    }

    // The languages a title can be translated into. The server's list is the only source, so the page
    // never offers a code the server refuses. Any error gives [] and the control is simply not added.
    function getTranslationTargets() {
        if (translationTargets !== null) return Promise.resolve(translationTargets);
        try {
            var url = ApiClient.getUrl('Plugins/WhisperSubs/TranslationTargets');
            return ApiClient.ajax({ type: 'GET', url: url }).then(function (resp) {
                var list = typeof resp === 'string' ? JSON.parse(resp) : resp;
                translationTargets = Array.isArray(list) ? list : [];
                return translationTargets;
            }).catch(function () { return []; });
        } catch (e) {
            return Promise.resolve([]);
        }
    }

    // Decide what this user can do with the WhisperSubs entry:
    //   'admin' → Generate Subtitles (drives generation directly)
    //   'user'  → Request Subtitles (submits a request, subject to approval/quota)
    //   'none'  → nothing to show
    function resolveMode() {
        return checkAdmin().then(function (admin) {
            if (admin) return { mode: 'admin' };
            return getCapabilities().then(function (c) {
                return { mode: (c && c.enabled) ? 'user' : 'none' };
            });
        }).catch(function () { return { mode: 'none' }; });
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

    function requestSubtitles(itemId) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/Request', { language: 'auto' });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function translateItem(itemId, target) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/Translate', { target: target });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    function requestTranslation(itemId, target) {
        var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/Request', { language: 'auto', target: target });
        return ApiClient.ajax({ type: 'POST', url: url });
    }

    // A viewer's translation errors map to fixed text by status only: no server text reaches a viewer.
    function userTranslateErrorText(xhr) {
        var status = xhr && xhr.status;
        if (status === 409) return 'WhisperSubs: That language cannot be made on this server right now';
        if (status === 400) return 'WhisperSubs: This title cannot be translated';
        return userRequestErrorText(xhr);
    }

    // An admin sees the server's reason when the rejection carries a JSON body; otherwise the status.
    function adminTranslateErrorText(xhr) {
        var fallback = 'WhisperSubs: Could not queue the translation (HTTP ' + ((xhr && xhr.status) || '?') + ')';
        if (!xhr || typeof xhr.json !== 'function') return Promise.resolve(fallback);
        return xhr.json().then(function (body) {
            return body && typeof body.error === 'string' && body.error ? 'WhisperSubs: ' + body.error : fallback;
        }).catch(function () { return fallback; });
    }

    function runTranslate(mode, itemId, target, name, setStatus) {
        if (mode === 'admin') {
            setStatus('WhisperSubs: Queuing translation...');
            return translateItem(itemId, target).then(function (response) {
                var data = typeof response === 'string' ? JSON.parse(response) : response;
                var count = data && data.queued != null ? data.queued : 0;
                setStatus('WhisperSubs: Queued ' + count + ' title(s) for ' + name +
                    '. Titles that already have it, or whose audio is already ' + name + ', are skipped.');
            }).catch(function (xhr) {
                return adminTranslateErrorText(xhr).then(setStatus);
            });
        }
        setStatus('WhisperSubs: Requesting translation...');
        return requestTranslation(itemId, target).then(function (response) {
            var data = typeof response === 'string' ? JSON.parse(response) : response;
            setStatus(userRequestResultText(data));
        }).catch(function (xhr) {
            setStatus(userTranslateErrorText(xhr));
        });
    }

    // "Translate into…" next to the Subtitles button. Native <select>; option labels and every status
    // message go through textContent.
    function injectTranslateControl(row, anchor, mode, itemId) {
        getTranslationTargets().then(function (targets) {
            if (!targets || targets.length === 0) return;
            if (row.querySelector('.btnWhisperSubsTranslate')) return; // guard against a double inject

            var select = document.createElement('select');
            select.className = 'btnWhisperSubsTranslate detailButton';
            select.setAttribute('aria-label', 'Translate into');

            var placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = 'Translate into\u2026';
            select.appendChild(placeholder);

            var names = {};
            for (var i = 0; i < targets.length; i++) {
                var t = targets[i];
                if (!t || typeof t.code !== 'string') continue;
                var opt = document.createElement('option');
                opt.value = t.code;
                opt.textContent = typeof t.name === 'string' ? t.name : t.code;
                names[t.code] = opt.textContent;
                select.appendChild(opt);
            }

            var status = document.createElement('span');
            status.className = 'whisperSubsTranslateStatus';
            status.setAttribute('role', 'status');
            function setStatus(text) { status.textContent = text; }

            select.addEventListener('change', function () {
                var code = select.value;
                if (!code) return;
                select.disabled = true;
                runTranslate(mode, itemId, code, names[code] || code, setStatus).then(function () {
                    select.value = '';
                    setTimeout(function () { select.disabled = false; }, 3000); // debounce repeat picks
                });
            });

            if (anchor && anchor.parentNode === row) {
                anchor.insertAdjacentElement('afterend', select);
            } else {
                row.appendChild(select);
            }
            select.insertAdjacentElement('afterend', status);
        });
    }

    function getItemRequestStatus(itemId) {
        try {
            var url = ApiClient.getUrl('Plugins/WhisperSubs/Items/' + itemId + '/RequestStatus');
            return ApiClient.ajax({ type: 'GET', url: url }).then(function (resp) {
                return typeof resp === 'string' ? JSON.parse(resp) : resp;
            }).catch(function () { return null; });
        } catch (e) {
            return Promise.resolve(null);
        }
    }

    // Client-side result text for a user request. Branches on the server's state ENUM name only (a
    // constant), never echoing server free-text — keeps this XSS-safe even though titles/usernames can
    // contain markup.
    function userRequestResultText(data) {
        var state = data && data.state;
        if (state === 'Queued') return 'WhisperSubs: Requested — added to the queue';
        if (state === 'Pending') return 'WhisperSubs: Requested — pending admin approval';
        return 'WhisperSubs: Subtitle request submitted';
    }

    function userRequestErrorText(xhr) {
        var status = xhr && xhr.status;
        if (status === 429) return 'WhisperSubs: You have reached your request limit — try again later';
        if (status === 503) return 'WhisperSubs: The request queue is full — try again later';
        return 'WhisperSubs: Could not submit request';
    }

    function runAction(mode, itemId) {
        if (mode === 'admin') {
            showToast('WhisperSubs: Queuing...');
            return generateSubtitles(itemId).then(function (response) {
                var data = typeof response === 'string' ? JSON.parse(response) : response;
                var count = data && data.queued != null ? data.queued : (data && data.count) || 1;
                showToast('WhisperSubs: Queued ' + count + ' item(s) for subtitle generation');
            }).catch(function () {
                showToast('WhisperSubs: Failed to queue generation');
            });
        }
        showToast('WhisperSubs: Requesting...');
        return requestSubtitles(itemId).then(function (response) {
            var data = typeof response === 'string' ? JSON.parse(response) : response;
            showToast(userRequestResultText(data));
        }).catch(function (xhr) {
            showToast(userRequestErrorText(xhr));
        });
    }

    function createMenuItem(itemId, mode) {
        var label = mode === 'admin' ? 'Generate Subtitles' : 'Request Subtitles';

        // Match Jellyfin's exact action sheet button structure.
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.type = 'button';
        btn.className = 'listItem listItem-button actionSheetMenuItem btnWhisperSubs';
        btn.setAttribute('data-id', 'whispersubs');

        btn.innerHTML =
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
                '<div class="listItemBodyText actionSheetItemText"></div>' +
            '</div>';
        // Label via textContent (never innerHTML) so the injected payload stays static/safe.
        btn.querySelector('.actionSheetItemText').textContent = label;

        btn.addEventListener('click', function () {
            closeDialog(btn);
            runAction(mode, itemId);
        });

        return btn;
    }

    function injectIntoActionSheet(sheet) {
        if (!pendingItemId) return;
        if (sheet.querySelector('.btnWhisperSubs')) return;

        resolveMode().then(function (info) {
            if (info.mode === 'none') return;
            if (sheet.querySelector('.btnWhisperSubs')) return; // re-check after async to avoid a double-inject

            var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
            var cancelDiv = scroller.querySelector('.buttons');
            var menuItem = createMenuItem(pendingItemId, info.mode);

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

    // Inject a visible "Generate/Request Subtitles" button onto the item detail page (issue #94/#112),
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

            resolveMode().then(function (info) {
                if (info.mode === 'none') return;
                if (row.querySelector('.btnWhisperSubsDetail')) return; // re-check after async

                var label = info.mode === 'admin' ? 'Subtitles' : 'Request Subs';

                var btn = document.createElement('button');
                btn.setAttribute('is', 'emby-button');
                btn.type = 'button';
                btn.className = 'button-flat detailButton emby-button btnWhisperSubsDetail';
                btn.title = info.mode === 'admin' ? 'Generate subtitles' : 'Request subtitles';
                btn.innerHTML =
                    '<div class="detailButton-content">' +
                        '<span class="material-icons detailButton-icon subtitles" aria-hidden="true"></span>' +
                        '<span class="detailButton-icon-text"></span>' +
                    '</div>';
                // Label via textContent (never innerHTML) so the injected payload stays static/safe.
                btn.querySelector('.detailButton-icon-text').textContent = label;

                btn.addEventListener('click', function (e) {
                    e.preventDefault();
                    if (btn.disabled) return;
                    btn.disabled = true;
                    runAction(info.mode, itemId).then(function () {
                        setTimeout(function () { btn.disabled = false; }, 3000); // debounce double-clicks
                    });
                });

                row.appendChild(btn);
                injectTranslateControl(row, btn, info.mode, itemId);

                // For a user, reflect any existing active request on the button (persistent feedback).
                if (info.mode === 'user') {
                    getItemRequestStatus(itemId).then(function (st) {
                        if (st && st.state) {
                            btn.querySelector('.detailButton-icon-text').textContent = (st.state === 'Pending') ? 'Requested' : 'Queued';
                            btn.title = (st.state === 'Pending') ? 'Subtitle request pending approval' : 'Subtitle request queued';
                            btn.disabled = true;
                        }
                    });
                }
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

    // Visible (console.log, not console.debug which browsers hide by default) so an admin can confirm
    // in DevTools that the injected script actually loaded — and see which mode applies. (Issue #94/#112.)
    console.log('[WhisperSubs] client script loaded');
    resolveMode().then(function (info) {
        if (info.mode === 'admin') {
            console.log('[WhisperSubs] administrator — "Generate Subtitles" button + menu and "Translate into" list enabled');
        } else if (info.mode === 'user') {
            console.log('[WhisperSubs] user requests enabled — "Request Subtitles" button + menu and "Translate into" list enabled');
        } else {
            console.log('[WhisperSubs] no WhisperSubs entry: you are not an administrator and user requests are disabled by the admin');
        }
    });
})();
