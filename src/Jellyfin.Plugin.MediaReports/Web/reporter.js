/* Media Reports: adds a "Report a problem" button to jellyfin-web, after the favourite
 * button on movie, episode, season and series pages and in the video player, and the
 * report dialog it opens. Served by the plugin and added to jellyfin-web's index page
 * as it is served. Written for the older browsers of TV apps: no modules, arrow
 * functions or classes. */
(function () {
    'use strict';

    var LABEL = 'Report a problem';
    var BUTTON_CLASS = 'mediaReportsButton';
    var REPORTABLE = { Movie: true, Episode: true, Season: true, Series: true };
    var MAX_NOTE = 1000;
    var CLOSE_AFTER_SENT_MS = 4000;

    // Item types by id: a type string once known, false while being looked up.
    var types = {};

    function warn(message, error) {
        try {
            window.console.warn('[Media Reports] ' + message, error || '');
        } catch (ignored) {
            // No console: nothing to do.
        }
    }

    function isTv() {
        return document.documentElement.className.indexOf('layout-tv') !== -1;
    }

    // jellyfin-web's ApiClient, once it exists and someone is signed in.
    function client() {
        var api = window.ApiClient;
        return api && typeof api.getCurrentUserId === 'function' && api.getCurrentUserId() ? api : null;
    }

    function el(tag, className, text) {
        var element = document.createElement(tag);
        if (className) {
            element.className = className;
        }
        if (text !== undefined) {
            element.textContent = text;
        }
        return element;
    }

    // ---- The Report button ------------------------------------------------------------

    function withType(itemId, done) {
        if (typeof types[itemId] === 'string') {
            done(types[itemId]);
            return;
        }

        var api = client();
        if (!api || types[itemId] === false) {
            return;
        }

        types[itemId] = false;
        api.getItem(api.getCurrentUserId(), itemId).then(function (item) {
            types[itemId] = item && item.Type ? item.Type : '';
            done(types[itemId]);
        }, function (error) {
            delete types[itemId];
            warn('could not look up item ' + itemId, error);
        });
    }

    function report(itemId, fromPlayer) {
        var detail = { itemId: itemId, fromPlayer: fromPlayer };
        var event;
        if (typeof window.CustomEvent === 'function') {
            event = new window.CustomEvent('mediareports:report', { detail: detail });
        } else {
            event = document.createEvent('CustomEvent');
            event.initCustomEvent('mediareports:report', false, false, detail);
        }

        document.dispatchEvent(event);
    }

    function makeButton(itemId, inPlayer) {
        var button = el('button');
        button.type = 'button';
        button.title = LABEL;
        button.setAttribute('aria-label', LABEL);
        button.setAttribute('data-id', itemId);
        var icon = el('span');
        icon.setAttribute('aria-hidden', 'true');
        if (inPlayer) {
            button.className = 'autoSize paper-icon-button-light ' + BUTTON_CLASS + (isTv() ? ' show-focus' : '');
            icon.className = 'xlargePaperIconButton material-icons flag';
            button.appendChild(icon);
        } else {
            button.className = 'button-flat detailButton emby-button ' + BUTTON_CLASS + (isTv() ? ' show-focus' : '');
            icon.className = 'material-icons detailButton-icon flag';
            var content = el('div', 'detailButton-content');
            content.appendChild(icon);
            button.appendChild(content);
        }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            report(button.getAttribute('data-id'), inPlayer);
        });
        return button;
    }

    function isOurs(element) {
        return !!element && (' ' + element.className + ' ').indexOf(' ' + BUTTON_CLASS + ' ') !== -1;
    }

    // Puts one button right after a favourite button, for the item that button rates.
    function place(favourite, inPlayer) {
        var itemId = favourite.getAttribute('data-id');
        var next = favourite.nextElementSibling;
        if (isOurs(next) && next.getAttribute('data-id') === itemId) {
            return;
        }

        // The player reuses its controls for the next item: drop a button for another one.
        var strays = favourite.parentNode.querySelectorAll('.' + BUTTON_CLASS);
        for (var i = 0; i < strays.length; i++) {
            strays[i].parentNode.removeChild(strays[i]);
        }

        if (!itemId) {
            return;
        }

        if (typeof types[itemId] !== 'string') {
            // jellyfin-web may rebuild the row while the type is looked up: place again once known.
            withType(itemId, placeAll);
            return;
        }

        if (REPORTABLE[types[itemId]]) {
            favourite.parentNode.insertBefore(makeButton(itemId, inPlayer), favourite.nextSibling);
        }
    }

    // ---- The Dashboard menu count ----------------------------------------------------------

    var MENU_LINK = 'a[href$="configurationpage?name=mediareports"]';
    var MENU_COUNT = 'mediaReportsMenuCount';
    var menuCount = { value: null, at: 0, loading: false, link: null };

    // The count of issues new to this admin, beside the Media Reports entry in the Dashboard
    // menu. Only admins see the entry, so only they ask for the count. It is asked again each
    // time the Dashboard draws its menu, and at most once a minute while it stays open.
    function placeMenuCount(refresh) {
        var link = document.querySelector(MENU_LINK);
        if (!link || !client()) {
            return;
        }
        if (refresh || link !== menuCount.link || menuCount.value === null || Date.now() - menuCount.at > 60000) {
            menuCount.link = link;
            if (!menuCount.loading) {
                menuCount.loading = true;
                call('GET', 'MediaReports/Admin/Summary').then(function (summary) {
                    menuCount = { value: summary.New, at: Date.now(), loading: false, link: link };
                    placeMenuCount(false);
                }, function () {
                    menuCount = { value: 0, at: Date.now(), loading: false, link: link };
                });
            }
            if (menuCount.value === null) {
                return;
            }
        }
        var badge = link.querySelector('.' + MENU_COUNT);
        if (!menuCount.value) {
            if (badge) {
                badge.parentNode.removeChild(badge);
            }
            return;
        }
        if (!badge) {
            badge = el('span', MENU_COUNT);
            badge.setAttribute('style', 'margin-left:auto;min-width:22px;height:20px;border-radius:10px;background:#00a4dc;color:rgba(0,0,0,.87);'
                + 'font-size:12px;font-weight:700;display:inline-flex;align-items:center;justify-content:center;padding:0 6px;box-sizing:border-box');
            link.appendChild(badge);
        }
        var text = String(menuCount.value);
        if (badge.textContent !== text) {
            badge.textContent = text;
            badge.setAttribute('aria-label', menuCount.value + ' new');
        }
    }

    function placeAll() {
        try {
            placeMenuCount(false);
            refreshUnread(false);
            placeEntries();
            var i;
            var pages = document.querySelectorAll('.mainDetailButtons .btnUserRating');
            for (i = 0; i < pages.length; i++) {
                place(pages[i], false);
            }

            var players = document.querySelectorAll('.osdControls .buttons .btnUserRating');
            for (i = 0; i < players.length; i++) {
                place(players[i], true);
            }
        } catch (error) {
            // Whatever jellyfin-web changes, a missing anchor only costs the button.
            warn('could not place the Report button', error);
        }
    }

    // ---- The report dialog ------------------------------------------------------------

    // The approved design's values, matching jellyfin-web 12's dark theme.
    var STYLES = ''
        + '.mediaReportsBackdrop{position:fixed;top:0;right:0;bottom:0;left:0;z-index:1100;background:rgba(0,0,0,.5);}'
        + '.mediaReportsBackdrop.mr-clear{background:transparent;pointer-events:none;}'
        + '.mediaReportsDialog{position:fixed;z-index:1101;box-sizing:border-box;background:#202020;color:rgba(255,255,255,.87);'
        + 'font-family:inherit;font-size:16px;line-height:1.4;padding:24px;overflow-y:auto;display:flex;flex-direction:column;}'
        + '.mediaReportsDialog.mr-desktop{width:640px;max-width:calc(100vw - 32px);max-height:calc(100vh - 64px);left:50%;top:50%;'
        + 'transform:translate(-50%,-50%);border-radius:4px;}'
        + '.mediaReportsDialog.mr-phone,.mediaReportsDialog.mr-tv{top:0;left:0;width:100vw;height:100vh;}'
        + '.mediaReportsDialog.mr-tv{padding:48px 72px;font-size:20px;}'
        + '.mediaReportsDialog.mr-tv>*{width:100%;max-width:880px;margin-left:auto;margin-right:auto;}'
        + '.mediaReportsDialog.mr-panel{top:0;right:0;width:400px;max-width:100vw;height:100vh;}'
        + '.mediaReportsDialog.mr-panel.mr-remote{width:480px;}'
        // Spacing uses margins, not flex gap, which older webOS browsers lack.
        + '.mediaReportsHeader{display:flex;align-items:flex-start;justify-content:space-between;}'
        + '.mediaReportsHeading{margin:0;font-size:1.4em;font-weight:500;}'
        + '.mediaReportsHeadingRow{display:flex;align-items:center;}'
        + '.mediaReportsTitle{margin:4px 0 0;color:rgba(255,255,255,.7);}'
        + '.mediaReportsClose{margin-left:16px;background:none;border:0;color:inherit;cursor:pointer;padding:4px;border-radius:50%;}'
        + '.mediaReportsLabel{margin:24px 0 8px;font-weight:500;}'
        + '.mediaReportsReasons{display:flex;flex-wrap:wrap;margin:-4px;}'
        + '.mediaReportsReason{margin:4px;height:40px;padding:0 16px;border-radius:20px;border:1px solid rgba(255,255,255,.3);background:transparent;'
        + 'color:inherit;font:inherit;cursor:pointer;}'
        + '.mr-phone .mediaReportsReason{height:44px;border-radius:22px;}'
        + '.mediaReportsReason[aria-pressed=true]{background:#00a4dc;border-color:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsReason:disabled{opacity:.4;cursor:default;}'
        + '.mediaReportsNote{box-sizing:border-box;width:100%;min-height:96px;background:rgba(255,255,255,.08);color:inherit;font:inherit;'
        + 'border:1px solid rgba(255,255,255,.3);border-radius:4px;padding:8px;resize:vertical;}'
        + '.mediaReportsCount{margin-top:4px;font-size:.85em;color:rgba(255,255,255,.6);text-align:right;}'
        + '.mediaReportsPlayback,.mediaReportsHint{margin:16px 0 0;color:rgba(255,255,255,.7);font-size:.9em;}'
        + '.mediaReportsInfo,.mediaReportsError{margin:16px 0 0;padding:12px 16px;border-radius:4px;}'
        + '.mediaReportsInfo{background:rgba(0,164,220,.12);border-left:4px solid #00a4dc;}'
        + '.mediaReportsError{background:rgba(244,67,54,.15);border-left:4px solid #f44336;}'
        + '.mediaReportsActions{margin-top:24px;display:flex;}'
        + '.mediaReportsActions>*+*{margin-left:12px;}'
        + '.mediaReportsSend,.mediaReportsDone,.mediaReportsCancel{flex:1;min-height:44px;border:0;border-radius:4px;font:inherit;font-weight:500;cursor:pointer;}'
        + '.mediaReportsSend,.mediaReportsDone{background:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsSend:disabled{background:rgba(255,255,255,.12);color:rgba(255,255,255,.5);cursor:default;}'
        + '.mediaReportsCancel{background:rgba(255,255,255,.12);color:inherit;}'
        // With a remote only the focused button is highlighted, and the buttons are stacked.
        + '.mr-remote .mediaReportsActions{flex-direction:column;}'
        + '.mr-remote .mediaReportsActions>*+*{margin-left:0;margin-top:12px;}'
        + '.mr-remote .mediaReportsSend,.mr-remote .mediaReportsDone{flex:none;min-height:56px;background:rgba(255,255,255,.12);color:inherit;}'
        + '.mr-remote .mediaReportsCancel{flex:none;min-height:56px;}'
        // With a remote the reasons are a two-column list of equal rows with a radio mark.
        + '.mr-remote .mediaReportsReasons{margin:-6px;}'
        + '.mr-remote .mediaReportsReason{position:relative;box-sizing:border-box;width:calc(50% - 12px);height:56px;margin:6px;'
        + 'padding:0 16px 0 56px;border:0;border-radius:4px;background:rgba(255,255,255,.08);text-align:left;'
        + 'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}'
        + '.mr-remote .mediaReportsReason:before{content:"";position:absolute;left:20px;top:50%;width:18px;height:18px;margin-top:-11px;'
        + 'border:2px solid rgba(255,255,255,.6);border-radius:50%;}'
        + '.mr-remote .mediaReportsReason[aria-pressed=true]{background:rgba(0,164,220,.2);color:inherit;}'
        + '.mr-remote .mediaReportsReason[aria-pressed=true]:before{border-color:#00a4dc;background:#00a4dc;box-shadow:inset 0 0 0 3px #202020;}'
        + '.mr-panel.mr-remote .mediaReportsReason{height:48px;padding-left:46px;}'
        + '.mr-panel.mr-remote .mediaReportsReason:before{left:16px;}'
        + '.mr-panel.mr-remote .mediaReportsSend,.mr-panel.mr-remote .mediaReportsDone,.mr-panel.mr-remote .mediaReportsCancel{min-height:48px;}'
        + '.mr-remote .mediaReportsSend:disabled{background:transparent;box-shadow:inset 0 0 0 1px rgba(255,255,255,.2);color:rgba(255,255,255,.5);}'
        + '.mediaReportsSent{margin-top:24px;}'
        + '.mediaReportsSent strong{display:block;font-size:1.2em;margin-bottom:8px;}'
        + '.mediaReportsDialog.mr-remote *:focus{outline:none;background:#00a4dc;border-color:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsDialog.mr-remote .mediaReportsReason:focus:before{border-color:rgba(0,0,0,.87);}'
        + '.mediaReportsDialog.mr-remote .mediaReportsReason[aria-pressed=true]:focus:before{background:rgba(0,0,0,.87);box-shadow:inset 0 0 0 3px #00a4dc;}'
        + '.mediaReportsDialog :focus{outline:2px solid #00a4dc;outline-offset:2px;}'
        + '.mediaReportsDialog :focus:not(:focus-visible){outline:none;}'
        + '.mediaReportsDialog :focus-visible{outline:2px solid #00a4dc;outline-offset:2px;}'
        // My Reports: rows with an unread dot and a status pill, the detail, and Withdraw.
        + '.mediaReportsList{list-style:none;margin:16px 0 0;padding:0;}'
        + '.mediaReportsItem{display:flex;flex-wrap:wrap;align-items:center;margin:0 0 8px;border-radius:4px;background:rgba(255,255,255,.05);}'
        + '.mediaReportsRow{display:flex;flex:1;align-items:center;box-sizing:border-box;width:100%;min-width:0;min-height:64px;margin:0;padding:12px 16px;'
        + 'background:rgba(255,255,255,.05);border:0;border-radius:4px;color:inherit;font:inherit;text-align:left;cursor:pointer;}'
        + '.mediaReportsRow:hover{background:rgba(255,255,255,.1);}'
        + '.mediaReportsUnreadDot{flex:none;width:8px;height:8px;margin-right:12px;border-radius:50%;background:#00a4dc;}'
        + '.mediaReportsRowText{flex:1;min-width:0;}'
        + '.mediaReportsRow:not(.mr-unread) .mediaReportsRowText{margin-left:20px;}'
        + '.mediaReportsRowTitle{display:block;margin:0;font-size:1em;font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}'
        + '.mr-unread .mediaReportsRowTitle{font-weight:700;}'
        + '.mediaReportsRowMeta{display:block;margin-top:2px;font-size:.85em;color:rgba(255,255,255,.6);}'
        + '.mediaReportsPill{flex:none;margin-left:12px;padding:3px 10px;border-radius:12px;font-size:.8em;font-weight:500;white-space:nowrap;}'
        + '.mediaReportsPill.mr-new{background:rgba(0,164,220,.18);color:#4fc3ec;}'
        + '.mediaReportsPill.mr-looking{background:rgba(242,176,30,.18);color:#f5c451;}'
        + '.mediaReportsPill.mr-fixed{background:rgba(102,187,106,.18);color:#8fd193;}'
        + '.mediaReportsPill.mr-closed{background:rgba(255,255,255,.1);color:rgba(255,255,255,.75);}'
        + '.mediaReportsEmpty{margin:24px 0 0;font-size:1.1em;font-weight:500;}'
        + '.mediaReportsEmptyHint{margin:8px 0 0;color:rgba(255,255,255,.7);}'
        + '.mediaReportsItem .mediaReportsRow{width:auto;background:transparent;}'
        + '.mediaReportsItem:hover{background:rgba(255,255,255,.1);}'
        + '.mediaReportsRowAction{flex:none;display:inline-flex;align-items:center;justify-content:center;width:44px;height:44px;margin-right:10px;'
        + 'background:none;border:0;border-radius:50%;color:rgba(255,255,255,.75);cursor:pointer;}'
        + '.mediaReportsRowAction:hover{background:rgba(255,255,255,.12);color:#fff;}'
        + '.mediaReportsItem>.mediaReportsConfirm{flex-basis:100%;box-sizing:border-box;margin:0;border-radius:0 0 4px 4px;}'
        + '.mediaReportsClearArea{margin-top:16px;}'
        + '.mediaReportsClear{min-height:44px;padding:0 20px;background:transparent;border:1px solid rgba(255,255,255,.3);border-radius:4px;'
        + 'color:inherit;font:inherit;cursor:pointer;}'
        + '.mediaReportsBack{display:inline-flex;align-items:center;justify-content:center;margin:0 8px 0 -6px;padding:4px;background:none;border:0;'
        + 'border-radius:50%;color:inherit;cursor:pointer;}'
        + '.mediaReportsBack:hover{background:rgba(255,255,255,.1);}'
        + '.mediaReportsDetailHead{display:flex;align-items:center;justify-content:space-between;margin-top:8px;}'
        + '.mediaReportsDetailHead .mediaReportsRowTitle{font-size:1.2em;font-weight:500;white-space:normal;}'
        + '.mediaReportsFacts{margin:8px 0 0;}'
        + '.mediaReportsFacts dt{margin:16px 0 2px;font-size:.85em;color:rgba(255,255,255,.6);}'
        + '.mediaReportsFacts dd{margin:0;white-space:pre-wrap;word-wrap:break-word;}'
        + '.mediaReportsHistory{list-style:none;margin:0 0 0 5px;padding:0 0 0 16px;border-left:2px solid rgba(255,255,255,.2);}'
        + '.mediaReportsHistory li{position:relative;padding:4px 0;}'
        + '.mediaReportsHistory li:before{content:"";position:absolute;left:-23px;top:50%;width:12px;height:12px;margin-top:-6px;border-radius:50%;'
        + 'background:#202020;box-shadow:inset 0 0 0 2px rgba(255,255,255,.6);}'
        + '.mediaReportsHistory li:last-child:before{background:#00a4dc;box-shadow:none;}'
        + '.mediaReportsWhen{margin-left:8px;font-size:.85em;color:rgba(255,255,255,.6);}'
        + '.mediaReportsWithdraw{margin-top:24px;min-height:44px;padding:0 20px;background:transparent;border:1px solid rgba(255,255,255,.3);'
        + 'border-radius:4px;color:inherit;font:inherit;cursor:pointer;}'
        + '.mediaReportsConfirm{margin-top:24px;padding:16px;background:rgba(255,255,255,.06);border-radius:4px;}'
        + '.mediaReportsConfirmText{margin:0;font-weight:500;}'
        + '.mediaReportsConfirm .mediaReportsActions{margin-top:16px;}'
        + '.mediaReportsDanger{flex:1;min-height:44px;border:0;border-radius:4px;background:#d32f2f;color:#fff;font:inherit;font-weight:500;cursor:pointer;}'
        + '.mediaReportsMine button:disabled{opacity:.6;cursor:default;}';

    var open = null;

    function addStyles() {
        if (document.getElementById('mediaReportsStyles')) {
            return;
        }
        var style = el('style');
        style.id = 'mediaReportsStyles';
        style.textContent = STYLES;
        document.head.appendChild(style);
    }

    function call(method, path, query, body) {
        var api = client();
        if (!api) {
            return Promise.reject({ status: 0 });
        }
        // A DELETE and a Hide answer 204 with no body to parse.
        var request = { type: method, url: api.getUrl(path, query || {}) };
        if (method !== 'DELETE' && !/\/Hide$/.test(path)) {
            request.dataType = 'json';
        }
        if (body) {
            request.data = JSON.stringify(body);
            request.contentType = 'application/json';
        }
        return api.ajax(request);
    }

    // A refused call rejects with the fetch Response; its body names the error code.
    function errorCode(failure) {
        if (failure && typeof failure.json === 'function') {
            return failure.json().then(function (body) {
                return body && (body.Code || body.code) || String(failure.status);
            }, function () {
                return String(failure.status);
            });
        }
        return Promise.resolve('network');
    }

    function dayOf(iso) {
        var then = new Date(iso);
        var now = new Date();
        var days = Math.floor((new Date(now.getFullYear(), now.getMonth(), now.getDate()) - new Date(then.getFullYear(), then.getMonth(), then.getDate())) / 86400000);
        if (days <= 0) {
            return 'today';
        }
        if (days === 1) {
            return 'yesterday';
        }
        if (days < 7) {
            return 'on ' + then.toLocaleDateString(undefined, { weekday: 'long' });
        }
        return 'on ' + then.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    function statusPhrase(status) {
        if (status === 'New') {
            return "It's waiting to be looked at.";
        }
        if (status === 'Investigating' || status === 'ReplacedVerify') {
            return "It's being looked at.";
        }
        return '';
    }

    function focusables(root) {
        var all = root.querySelectorAll('button, textarea, [href], [tabindex]:not([tabindex="-1"])');
        var list = [];
        for (var i = 0; i < all.length; i++) {
            if (!all[i].disabled && all[i].offsetParent !== null) {
                list.push(all[i]);
            }
        }
        return list;
    }

    // Remote arrows move focus to the nearest control that way, inside the dialog only.
    function moveFocus(root, key) {
        var current = document.activeElement;
        var list = focusables(root);
        if (!root.contains(current)) {
            if (list.length) {
                list[0].focus();
            }
            return;
        }
        var from = current.getBoundingClientRect();
        var best = null;
        var bestScore = Infinity;
        for (var i = 0; i < list.length; i++) {
            if (list[i] === current) {
                continue;
            }
            var to = list[i].getBoundingClientRect();
            var dx = (to.left + to.width / 2) - (from.left + from.width / 2);
            var dy = (to.top + to.height / 2) - (from.top + from.height / 2);
            var ahead = key === 'ArrowRight' ? dx > 1 && Math.abs(dy) < from.height
                : key === 'ArrowLeft' ? dx < -1 && Math.abs(dy) < from.height
                    : key === 'ArrowDown' ? dy > 1 : dy < -1;
            if (!ahead) {
                continue;
            }
            var score = key === 'ArrowRight' || key === 'ArrowLeft' ? Math.abs(dx) + Math.abs(dy) * 4 : Math.abs(dy) + Math.abs(dx) / 4;
            if (score < bestScore) {
                bestScore = score;
                best = list[i];
            }
        }
        if (!best && (key === 'ArrowRight' || key === 'ArrowDown')) {
            best = list[list.indexOf(current) + 1] || null;
        }
        if (best) {
            best.focus();
        }
    }

    function close() {
        if (!open) {
            return;
        }
        var state = open;
        open = null;
        window.clearTimeout(state.timer);
        document.removeEventListener('keydown', state.onKey, true);
        if (state.backdrop.parentNode) {
            state.backdrop.parentNode.removeChild(state.backdrop);
        }
        if (state.dialog.parentNode) {
            state.dialog.parentNode.removeChild(state.dialog);
        }
        if (state.returnTo && document.body.contains(state.returnTo)) {
            state.returnTo.focus();
        }
        if (state.onClose) {
            state.onClose();
        }
    }

    // Opens an empty dialog in the given layout, with its heading, keys and focus trap.
    // BACK and ESC call state.onBack first when it is set and returns true.
    function openFrame(state, heading, extraClass) {
        close();
        addStyles();
        var layout = state.layout;
        var tv = state.tv;
        state.timer = 0;
        state.returnTo = document.activeElement;
        open = state;

        state.backdrop = el('div', 'mediaReportsBackdrop' + (layout === 'panel' ? ' mr-clear' : ''));
        state.backdrop.addEventListener('click', close);
        var dialog = el('div', 'mediaReportsDialog mr-' + layout + (tv ? ' mr-remote' : '') + (extraClass ? ' ' + extraClass : ''));
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.setAttribute('aria-label', heading);
        state.dialog = dialog;

        var header = el('div', 'mediaReportsHeader');
        var titles = el('div');
        state.headingRow = el('div', 'mediaReportsHeadingRow');
        state.headingRow.appendChild(el('h2', 'mediaReportsHeading', heading));
        titles.appendChild(state.headingRow);
        state.title = el('p', 'mediaReportsTitle', '');
        titles.appendChild(state.title);
        header.appendChild(titles);
        if (!tv) {
            var closeButton = el('button', 'mediaReportsClose');
            closeButton.type = 'button';
            closeButton.setAttribute('aria-label', 'Close');
            closeButton.title = 'Close';
            closeButton.appendChild(el('span', 'material-icons close'));
            closeButton.firstChild.setAttribute('aria-hidden', 'true');
            closeButton.addEventListener('click', close);
            header.appendChild(closeButton);
        }
        dialog.appendChild(header);
        state.body = el('div');
        state.body.appendChild(el('p', 'mediaReportsHint', 'Loading…'));
        dialog.appendChild(state.body);

        // Keys stay inside the dialog: a typed space never reaches the player behind it.
        state.onKey = function (event) {
            if (!open || !dialog.contains(event.target) && event.target !== document.body && !dialog.contains(document.activeElement)) {
                return;
            }
            var typing = event.target && event.target.tagName === 'TEXTAREA';
            var key = event.key;
            if (key === 'Escape' || event.keyCode === 461 || key === 'GoBack' || key === 'Backspace' && !typing) {
                event.preventDefault();
                event.stopPropagation();
                if (!state.onBack || !state.onBack()) {
                    close();
                }
                return;
            }
            if (key === 'Tab') {
                var list = focusables(dialog);
                if (list.length) {
                    var index = list.indexOf(document.activeElement);
                    var next = event.shiftKey ? (index <= 0 ? list.length - 1 : index - 1) : (index + 1) % list.length;
                    list[next].focus();
                }
                event.preventDefault();
            } else if (/^Arrow/.test(key) && !typing && tv) {
                moveFocus(dialog, key);
                event.preventDefault();
            }
            event.stopPropagation();
        };
        document.addEventListener('keydown', state.onKey, true);

        document.body.appendChild(state.backdrop);
        document.body.appendChild(dialog);
        dialog.setAttribute('tabindex', '-1');
        dialog.focus();
        return state;
    }

    function openDialog(itemId, fromPlayer) {
        // The player keeps the video in view with a side panel, also in the TV layout.
        var tv = isTv();
        var layout = fromPlayer ? 'panel' : tv ? 'tv' : window.innerWidth < 600 ? 'phone' : 'desktop';
        var state = openFrame({ itemId: itemId, fromPlayer: fromPlayer, layout: layout, tv: tv, reason: null, sending: false }, LABEL);

        call('GET', 'MediaReports/Report/Context', { itemId: itemId }).then(function (context) {
            if (open === state) {
                render(state, context);
            }
        }, function (failure) {
            errorCode(failure).then(function (code) {
                if (open !== state) {
                    return;
                }
                state.body.innerHTML = '';
                state.body.appendChild(el('p', 'mediaReportsError', code === 'reporting-off'
                    ? 'Reporting is switched off on this server.'
                    : "Couldn't reach the server. Try again in a moment."));
            });
        });
    }

    function render(state, context) {
        var layout = state.layout;
        state.context = context;
        state.title.textContent = state.fromPlayer ? context.Name + '. Playback keeps going.' : context.Name;
        var body = state.body;
        body.innerHTML = '';

        var blocked = {};
        var open_ = context.OpenReports || [];
        if (open_.length) {
            var first = open_[0];
            for (var j = 0; j < open_.length; j++) {
                blocked[open_[j].ReasonCode] = true;
            }
            body.appendChild(el('p', 'mediaReportsInfo', 'You reported this ' + (first.CreatedAt ? dayOf(first.CreatedAt) : 'already') + ': '
                + first.ReasonLabel + '. ' + statusPhrase(first.Status) + ' Send another only if something else is wrong.'));
        }

        state.error = el('p', 'mediaReportsError');
        state.error.style.display = 'none';
        state.error.setAttribute('role', 'alert');
        body.appendChild(state.error);

        var label = el('p', 'mediaReportsLabel', "What's wrong?");
        label.id = 'mediaReportsReasonsLabel';
        body.appendChild(label);
        var reasons = el('div', 'mediaReportsReasons');
        reasons.setAttribute('role', 'group');
        reasons.setAttribute('aria-labelledby', label.id);
        state.chips = [];
        (context.Reasons || []).forEach(function (reason) {
            var chip = el('button', 'mediaReportsReason', reason.Label);
            chip.type = 'button';
            chip.setAttribute('aria-pressed', 'false');
            chip.setAttribute('data-code', reason.Code);
            chip.disabled = !!blocked[reason.Code];
            chip.addEventListener('click', function () {
                if (state.sending) {
                    return;
                }
                state.reason = reason.Code;
                state.chips.forEach(function (c) {
                    c.setAttribute('aria-pressed', c === chip ? 'true' : 'false');
                });
                state.send.disabled = false;
            });
            state.chips.push(chip);
            reasons.appendChild(chip);
        });
        body.appendChild(reasons);

        if (state.tv) {
            body.appendChild(el('p', 'mediaReportsHint', 'Typing a note with a remote is slow, so the TV asks for a reason only.'));
        } else {
            var noteLabel = el('label', 'mediaReportsLabel', 'Anything else? (optional)');
            noteLabel.setAttribute('for', 'mediaReportsNote');
            noteLabel.style.display = 'block';
            body.appendChild(noteLabel);
            state.note = el('textarea', 'mediaReportsNote');
            state.note.id = 'mediaReportsNote';
            var count = el('div', 'mediaReportsCount', '0 / 1,000');
            state.note.addEventListener('input', function () {
                count.textContent = state.note.value.length.toLocaleString() + ' / 1,000';
            });
            body.appendChild(state.note);
            body.appendChild(count);
        }

        var playback = context.Playback;
        if (playback) {
            var line = state.fromPlayer && playback.State === 'Playing'
                ? "How it's playing right now is attached."
                : 'How you last played it, on ' + (playback.DeviceName || 'another device') + ' ' + dayOf(playback.LastSeen)
                    + ', is attached so the admin can see what went wrong.';
            body.appendChild(el('p', 'mediaReportsPlayback', line));
        }

        var actions = el('div', 'mediaReportsActions');
        state.send = el('button', 'mediaReportsSend', 'Send report');
        state.send.type = 'button';
        state.send.disabled = true;
        state.send.addEventListener('click', function () {
            submit(state);
        });
        actions.appendChild(state.send);
        if (state.tv) {
            var cancel = el('button', 'mediaReportsCancel', 'Cancel');
            cancel.type = 'button';
            cancel.addEventListener('click', close);
            actions.appendChild(cancel);
        }
        body.appendChild(actions);

        var start = focusables(reasons)[0] || state.send;
        start.focus();
    }

    function showError(state, message) {
        state.error.textContent = message;
        state.error.style.display = '';
    }

    function submit(state) {
        if (!state.reason || state.sending) {
            return;
        }
        state.sending = true;
        state.send.disabled = true;
        state.send.textContent = 'Sending…';
        state.error.style.display = 'none';
        var body = { ItemId: state.itemId, ReasonCode: state.reason };
        if (state.note && state.note.value.trim()) {
            body.Note = state.note.value;
        }

        call('POST', 'MediaReports/Report', null, body).then(function () {
            if (open === state) {
                sent(state);
            }
        }, function (failure) {
            errorCode(failure).then(function (code) {
                if (open !== state) {
                    return;
                }
                state.sending = false;
                state.send.disabled = false;
                state.send.textContent = 'Try again';
                if (code === 'note-too-long') {
                    showError(state, 'Your note is too long. Keep it to 1,000 characters and send again.');
                } else if (code === 'already-reported') {
                    showError(state, "You already reported this title for that reason. You'll see its outcome in My Reports.");
                } else if (code === 'reporting-off') {
                    showError(state, 'Reporting is switched off on this server.');
                } else {
                    showError(state, "Couldn't reach the server, so your report wasn't sent. Your choices are kept; try again.");
                }
                state.send.focus();
            });
        });
    }

    function sent(state) {
        var body = state.body;
        body.innerHTML = '';
        var done = el('div', 'mediaReportsSent');
        done.setAttribute('role', 'status');
        done.appendChild(el('strong', '', 'Report sent'));
        var text = state.tv && !state.fromPlayer
            ? "You'll find the outcome under Settings, My Reports."
            : state.fromPlayer
                ? "You'll see the outcome in My Reports. This closes by itself."
                : "Thanks. When it's sorted you'll see the outcome in My Reports, in your user menu.";
        done.appendChild(el('span', '', text));
        body.appendChild(done);
        if (state.fromPlayer) {
            state.timer = window.setTimeout(close, CLOSE_AFTER_SENT_MS);
            return;
        }
        var actions = el('div', 'mediaReportsActions');
        var ok = el('button', 'mediaReportsDone', state.tv ? 'OK' : 'Done');
        ok.type = 'button';
        ok.addEventListener('click', close);
        actions.appendChild(ok);
        body.appendChild(actions);
        ok.focus();
    }

    // ---- My Reports -----------------------------------------------------------------------

    var MINE_LABEL = 'My Reports';
    var PILLS = { New: 'mr-new', Investigating: 'mr-looking', ReplacedVerify: 'mr-looking', Fixed: 'mr-fixed' };
    var DOT = 'mediaReportsDot';
    var USER_BUTTONS = 'button[aria-controls=app-user-menu], .headerUserButton';
    var SETTINGS_ITEM = 'a[role=menuitem][href="#/mypreferencesmenu"]';

    // The caller's unread count: read when jellyfin-web loads, for each signed-in user, and
    // again whenever My Reports closes.
    var unread = { user: null, value: null, loading: false };

    function refreshUnread(force) {
        var api = client();
        if (!api || unread.loading) {
            return;
        }
        var user = api.getCurrentUserId();
        if (!force && unread.user === user && unread.value !== null) {
            return;
        }
        unread.loading = true;
        call('GET', 'MediaReports/Report/Mine').then(function (mine) {
            unread = { user: user, value: mine.Unread || 0, loading: false };
            placeEntries();
        }, function () {
            unread = { user: user, value: 0, loading: false };
        });
    }

    function shortDay(iso) {
        var then = new Date(iso);
        var phrase = dayOf(iso);
        if (phrase === 'today' || phrase === 'yesterday') {
            return phrase.charAt(0).toUpperCase() + phrase.slice(1);
        }
        return then.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    function pill(status, label) {
        return el('span', 'mediaReportsPill ' + (PILLS[status] || 'mr-closed'), label);
    }

    function opensOnEnter(element, action) {
        element.addEventListener('click', function (event) {
            event.preventDefault();
            action();
        });
        element.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                action();
            }
        });
    }

    // The entry after Settings in the User Menu, the row after Profile in Settings where
    // there is no User Menu (the TV layout), and the dot on the user button.
    function placeEntries() {
        var count = unread.value || 0;
        var i;
        var buttons = document.querySelectorAll(USER_BUTTONS);
        for (i = 0; i < buttons.length; i++) {
            var dot = buttons[i].querySelector('.' + DOT);
            if (count && !dot) {
                // On the button's corner above the user's picture, with a ring in the header's
                // colour so it stands apart from an avatar that is itself blue. The TV layout's
                // button is a circle that clips its corners, so it lets the dot overflow.
                dot = el('span', DOT);
                dot.setAttribute('aria-hidden', 'true');
                dot.setAttribute('style', 'position:absolute;top:2px;right:2px;z-index:2;width:10px;height:10px;border-radius:50%;background:#00a4dc;'
                    + 'box-shadow:0 0 0 2px ' + backgroundBehind(buttons[i]) + ';pointer-events:none');
                if (window.getComputedStyle(buttons[i]).position === 'static') {
                    buttons[i].style.position = 'relative';
                }
                dot.setAttribute('data-overflow', buttons[i].style.overflow);
                buttons[i].style.overflow = 'visible';
                buttons[i].appendChild(dot);
            } else if (!count && dot) {
                buttons[i].style.overflow = dot.getAttribute('data-overflow') || '';
                dot.parentNode.removeChild(dot);
            }
        }

        var settings = document.querySelector(SETTINGS_ITEM);
        if (settings) {
            placeMenuItem(settings, count);
        }

        var profile = document.querySelector('.page:not(.hide) a.lnkUserProfile');
        if (profile && (isTv() || !document.querySelector('button[aria-controls=app-user-menu]'))) {
            placeSettingsRow(profile, count);
        }
    }

    // The first solid background behind an element; a see-through header doesn't count.
    function backgroundBehind(element) {
        for (var node = element; node && node.nodeType === 1; node = node.parentNode) {
            var colour = window.getComputedStyle(node).backgroundColor;
            if (/^rgb\(/.test(colour)) {
                return colour;
            }
        }
        return '#101010';
    }

    function placeMenuItem(settings, count) {
        var item = settings.nextElementSibling;
        if (!item || (' ' + item.className + ' ').indexOf(' mediaReportsMenuItem ') === -1) {
            var stray = settings.parentNode.querySelector('.mediaReportsMenuItem');
            if (stray) {
                stray.parentNode.removeChild(stray);
            }
            // A copy of Settings keeps the menu's own look, with our icon, label and action.
            item = settings.cloneNode(true);
            item.removeAttribute('href');
            item.removeAttribute('id');
            item.className += ' mediaReportsMenuItem';
            item.setAttribute('tabindex', '-1');
            var icon = item.querySelector('svg');
            if (icon) {
                var flag = el('span', 'material-icons flag');
                flag.setAttribute('aria-hidden', 'true');
                flag.setAttribute('style', 'font-size:24px');
                icon.parentNode.replaceChild(flag, icon);
            }
            var text = item.querySelector('.MuiListItemText-primary') || item;
            text.textContent = MINE_LABEL;
            opensOnEnter(item, function () {
                // Close the User Menu the way a key would and open My Reports straight away.
                // The menu hands focus back to the user button once its closing animation
                // ends (a closed menu stays in the page, hidden), so My Reports takes focus
                // back then, and returns it to the user button when it closes.
                var userButton = document.querySelector('button[aria-controls=app-user-menu]');
                item.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
                openMine();
                var state = open;
                state.returnTo = userButton;
                var tries = 0;
                var whenGone = function () {
                    if (document.body.contains(item) && window.getComputedStyle(item).visibility !== 'hidden' && ++tries < 40) {
                        window.setTimeout(whenGone, 50);
                        return;
                    }
                    window.setTimeout(function () {
                        if (open === state && !state.dialog.contains(document.activeElement)) {
                            (focusables(state.body)[0] || focusables(state.dialog)[0] || state.dialog).focus();
                        }
                    }, 0);
                };
                window.setTimeout(whenGone, 50);
            });
            settings.parentNode.insertBefore(item, settings.nextSibling);
        }
        var badge = item.querySelector('.mediaReportsMenuItemCount');
        if (count && !badge) {
            badge = el('span', 'mediaReportsMenuItemCount');
            badge.setAttribute('style', 'margin-left:auto;padding-left:16px;min-width:22px;height:20px;box-sizing:content-box;display:inline-flex;align-items:center;justify-content:center');
            var number = el('span');
            number.setAttribute('style', 'min-width:22px;height:20px;border-radius:10px;background:#00a4dc;color:rgba(0,0,0,.87);font-size:12px;font-weight:700;'
                + 'display:inline-flex;align-items:center;justify-content:center;padding:0 6px;box-sizing:border-box');
            badge.appendChild(number);
            item.appendChild(badge);
        } else if (!count && badge) {
            badge.parentNode.removeChild(badge);
            badge = null;
        }
        if (badge && badge.textContent !== String(count)) {
            badge.firstChild.textContent = String(count);
        }
        item.setAttribute('aria-label', count ? MINE_LABEL + ', ' + count + ' updated' : MINE_LABEL);
    }

    function placeSettingsRow(profile, count) {
        var row = profile.nextElementSibling;
        if (!row || (' ' + row.className + ' ').indexOf(' mediaReportsSettingsRow ') === -1) {
            row = el('a', 'emby-button listItem-border show-focus mediaReportsSettingsRow');
            row.href = '#';
            row.setAttribute('style', 'display:block;margin:0;padding:0');
            var item = el('div', 'listItem');
            var icon = el('span', 'material-icons listItemIcon listItemIcon-transparent flag');
            icon.setAttribute('aria-hidden', 'true');
            item.appendChild(icon);
            var body = el('div', 'listItemBody');
            var label = el('div', 'listItemBodyText', MINE_LABEL);
            label.setAttribute('style', 'display:flex;align-items:center');
            body.appendChild(label);
            item.appendChild(body);
            row.appendChild(item);
            opensOnEnter(row, openMine);
            profile.parentNode.insertBefore(row, profile.nextSibling);
        }
        // The count is the same blue pill as the User Menu's, on the label's line.
        var line = row.querySelector('.listItemBodyText');
        var badge = row.querySelector('.mediaReportsSettingsRowCount');
        var text = count + ' updated';
        if (count && !badge) {
            badge = el('span', 'mediaReportsSettingsRowCount');
            badge.setAttribute('style', 'margin-left:16px;padding:2px 10px;border-radius:12px;background:#00a4dc;color:rgba(0,0,0,.87);'
                + 'font-size:.8em;font-weight:700;line-height:1.4;white-space:nowrap');
            line.appendChild(badge);
        } else if (!count && badge) {
            badge.parentNode.removeChild(badge);
            badge = null;
        }
        if (badge && badge.textContent !== text) {
            badge.textContent = text;
        }
        row.setAttribute('aria-label', count ? MINE_LABEL + ', ' + text : MINE_LABEL);
    }

    function openMine() {
        var tv = isTv();
        var state = openFrame({ layout: tv ? 'tv' : window.innerWidth < 600 ? 'phone' : 'desktop', tv: tv, mine: null }, MINE_LABEL, 'mediaReportsMine');
        state.onClose = function () {
            refreshUnread(true);
        };
        state.title.parentNode.removeChild(state.title);
        state.body.className = 'mediaReportsMineBody';
        loadMine(state, null);
    }

    function loadMine(state, focusId) {
        call('GET', 'MediaReports/Report/Mine').then(function (mine) {
            if (open === state) {
                state.mine = mine;
                renderMine(state, focusId);
            }
        }, function () {
            if (open === state) {
                state.body.innerHTML = '';
                state.body.appendChild(el('p', 'mediaReportsError', "Couldn't reach the server. Try again in a moment."));
            }
        });
    }

    function renderMine(state, focusId, message) {
        state.onBack = null;
        var headerBack = state.headingRow.querySelector('.mediaReportsBack');
        if (headerBack) {
            state.headingRow.removeChild(headerBack);
        }
        var body = state.body;
        body.innerHTML = '';
        if (message) {
            var problem = el('p', 'mediaReportsError', message);
            problem.setAttribute('role', 'alert');
            body.appendChild(problem);
        }
        var reports = state.mine.Reports || [];
        if (!reports.length) {
            body.appendChild(el('p', 'mediaReportsEmpty', "You haven't reported anything."));
            body.appendChild(el('p', 'mediaReportsEmptyHint', "When something won't play right, choose the flag button after the favourite button on the title's page, or in the player's controls."));
        }
        var list = el('ul', 'mediaReportsList');
        var focus = null;
        reports.forEach(function (report, index) {
            var li = el('li', 'mediaReportsItem');
            var row = el('button', 'mediaReportsRow' + (report.Unread ? ' mr-unread' : ''));
            row.type = 'button';
            if (report.Unread) {
                var dot = el('span', 'mediaReportsUnreadDot');
                dot.setAttribute('aria-hidden', 'true');
                row.appendChild(dot);
            }
            var text = el('span', 'mediaReportsRowText');
            text.appendChild(el('span', 'mediaReportsRowTitle', report.Title));
            var meta = report.ReasonLabel + ' · ' + shortDay(report.CreatedAt) + (report.ExpiresLabel ? ' · ' + report.ExpiresLabel : '');
            text.appendChild(el('span', 'mediaReportsRowMeta', meta));
            row.appendChild(text);
            row.appendChild(pill(report.Status, report.StatusLabel));
            row.setAttribute('aria-label', report.Title + ', ' + report.ReasonLabel + ', ' + report.StatusLabel
                + (report.Unread ? ', updated' : '') + (report.ExpiresLabel ? ', ' + report.ExpiresLabel.toLowerCase() : ''));
            row.addEventListener('click', function () {
                openReport(state, report);
            });
            if (report.Id === focusId) {
                focus = row;
            }
            li.appendChild(row);

            // One action per row, with a pointer only: the TV only reads.
            var next = reports[index + 1] || reports[index - 1];
            if (!state.tv && report.CanHide) {
                li.appendChild(rowAction('close', 'mediaReportsHide', 'Remove from list', function () {
                    hideReport(state, report, next);
                }));
            } else if (!state.tv && report.CanWithdraw) {
                var withdraw = rowAction('undo', 'mediaReportsRowWithdraw', 'Withdraw', null);
                withdraw.addEventListener('click', function () {
                    confirmWithdraw(state, report, li, withdraw, next);
                });
                li.appendChild(withdraw);
            }
            list.appendChild(li);
        });
        body.appendChild(list);

        var closed = reports.filter(function (r) {
            return r.CanHide;
        }).length;
        if (closed && !state.tv) {
            var footer = el('div', 'mediaReportsClearArea');
            var clear = el('button', 'mediaReportsClear', 'Clear all closed');
            clear.type = 'button';
            clear.addEventListener('click', function () {
                confirmClear(state, footer, clear, closed);
            });
            footer.appendChild(clear);
            body.appendChild(footer);
        }
        (focus || focusables(list)[0] || focusables(state.dialog)[0] || state.dialog).focus();
    }

    function rowAction(icon, className, label, action) {
        var button = el('button', 'mediaReportsRowAction ' + className);
        button.type = 'button';
        button.title = label;
        button.setAttribute('aria-label', label);
        var glyph = el('span', 'material-icons ' + icon);
        glyph.setAttribute('aria-hidden', 'true');
        button.appendChild(glyph);
        if (action) {
            button.addEventListener('click', action);
        }
        return button;
    }

    // Removing only tidies the user's own list, so it needs no confirmation.
    function hideReport(state, report, next) {
        call('POST', 'MediaReports/Report/Mine/' + report.Id + '/Hide').then(function () {
            if (open === state) {
                loadMine(state, next ? next.Id : null);
            }
        }, function () {
            if (open === state) {
                renderMine(state, report.Id, "Couldn't reach the server, so the report is still in your list. Try again.");
            }
        });
    }

    function confirmClear(state, area, clear, closed) {
        var text = closed === 1 ? 'Remove the closed report from your list?' : 'Remove ' + closed + ' closed reports from your list?';
        confirmIn(area, clear, {
            text: text,
            keep: 'Cancel',
            keepClass: 'mediaReportsClearCancel',
            go: 'Remove',
            goClass: 'mediaReportsClearConfirm',
            run: function () {
                return call('POST', 'MediaReports/Report/Mine/HideClosed');
            },
            done: function () {
                if (open === state) {
                    loadMine(state, null);
                }
            },
            failure: function () {
                return "Couldn't reach the server, so nothing was removed. Try again.";
            },
            alive: function () {
                return open === state;
            }
        });
    }

    function openReport(state, report) {
        state.body.innerHTML = '';
        state.body.appendChild(el('p', 'mediaReportsHint', 'Loading…'));
        call('GET', 'MediaReports/Report/Mine/' + report.Id).then(function (detail) {
            if (open !== state) {
                return;
            }
            // Opening it marked it read on the server.
            if (report.Unread) {
                report.Unread = false;
                state.mine.Unread = Math.max(0, (state.mine.Unread || 0) - 1);
            }
            renderReport(state, detail);
        }, function () {
            if (open === state) {
                renderMine(state, report.Id);
            }
        });
    }

    function renderReport(state, detail) {
        var back = function () {
            renderMine(state, detail.Id);
            return true;
        };
        state.onBack = back;
        var body = state.body;
        body.innerHTML = '';
        var view = el('div', 'mediaReportsDetail');
        // Back sits in the header before the heading; on TV the remote's BACK does it.
        if (!state.tv && !state.headingRow.querySelector('.mediaReportsBack')) {
            var backButton = el('button', 'mediaReportsBack');
            backButton.type = 'button';
            backButton.setAttribute('aria-label', 'Back to My Reports');
            backButton.title = 'Back';
            var arrow = el('span', 'material-icons arrow_back');
            arrow.setAttribute('aria-hidden', 'true');
            backButton.appendChild(arrow);
            backButton.addEventListener('click', back);
            state.headingRow.insertBefore(backButton, state.headingRow.firstChild);
        }
        var head = el('div', 'mediaReportsDetailHead');
        head.appendChild(el('h3', 'mediaReportsRowTitle', detail.Title));
        head.appendChild(pill(detail.Status, detail.StatusLabel));
        view.appendChild(head);

        var facts = el('dl', 'mediaReportsFacts');
        var fact = function (name, value) {
            if (value) {
                facts.appendChild(el('dt', '', name));
                facts.appendChild(el('dd', '', value));
            }
        };
        fact('Reason', detail.ReasonLabel);
        fact('Your note', detail.Note);
        fact('Watched on', detail.Device);
        view.appendChild(facts);

        view.appendChild(el('p', 'mediaReportsLabel', 'History'));
        var history = el('ul', 'mediaReportsHistory');
        (detail.History || []).forEach(function (step) {
            var li = el('li');
            li.appendChild(el('span', 'mediaReportsStep', step.Label));
            li.appendChild(el('span', 'mediaReportsWhen', shortDay(step.At)));
            history.appendChild(li);
        });
        view.appendChild(history);

        // Withdrawing needs a pointer: the TV only reads.
        if (detail.CanWithdraw && !state.tv) {
            var actions = el('div', 'mediaReportsWithdrawArea');
            var withdraw = el('button', 'mediaReportsWithdraw', 'Withdraw report');
            withdraw.type = 'button';
            withdraw.addEventListener('click', function () {
                confirmWithdraw(state, detail, actions, withdraw, null);
            });
            actions.appendChild(withdraw);
            view.appendChild(actions);
        }
        body.appendChild(view);
        (focusables(view)[0] || state.dialog).focus();
    }

    function confirmWithdraw(state, report, area, withdraw, next) {
        confirmIn(area, withdraw, {
            text: 'Withdraw this report?',
            keep: 'Keep it',
            keepClass: 'mediaReportsKeep',
            go: 'Withdraw',
            goClass: 'mediaReportsWithdrawConfirm',
            run: function () {
                return call('DELETE', 'MediaReports/Report/Mine/' + report.Id);
            },
            done: function () {
                if (open === state) {
                    loadMine(state, next ? next.Id : null);
                }
            },
            failure: function (code) {
                return code === 'not-new'
                    ? "It's already being looked at, so it can't be withdrawn."
                    : "Couldn't reach the server, so the report wasn't withdrawn. Try again.";
            },
            alive: function () {
                return open === state;
            }
        });
    }

    // Asks in place of the button that asked: the button hides, the question takes its
    // place, and Keep puts the button back.
    function confirmIn(area, asker, ask) {
        asker.style.display = 'none';
        var box = el('div', 'mediaReportsConfirm');
        box.setAttribute('role', 'alertdialog');
        box.setAttribute('aria-label', ask.text);
        box.appendChild(el('p', 'mediaReportsConfirmText', ask.text));
        var error = el('p', 'mediaReportsError');
        error.style.display = 'none';
        error.setAttribute('role', 'alert');
        box.appendChild(error);
        var buttons = el('div', 'mediaReportsActions');
        var keep = el('button', 'mediaReportsCancel ' + ask.keepClass, ask.keep);
        keep.type = 'button';
        var go = el('button', 'mediaReportsDanger ' + ask.goClass, ask.go);
        go.type = 'button';
        buttons.appendChild(keep);
        buttons.appendChild(go);
        box.appendChild(buttons);
        area.appendChild(box);
        keep.addEventListener('click', function () {
            area.removeChild(box);
            asker.style.display = '';
            asker.focus();
        });
        go.addEventListener('click', function () {
            go.disabled = true;
            keep.disabled = true;
            ask.run().then(ask.done, function (failure) {
                errorCode(failure).then(function (code) {
                    if (!ask.alive()) {
                        return;
                    }
                    go.disabled = false;
                    keep.disabled = false;
                    error.textContent = ask.failure(code);
                    error.style.display = '';
                });
            });
        });
        keep.focus();
    }

    // ---- Start --------------------------------------------------------------------------

    function start() {
        var queued = false;
        var observer = new window.MutationObserver(function () {
            if (!queued) {
                queued = true;
                window.setTimeout(function () {
                    queued = false;
                    placeAll();
                }, 50);
            }
        });
        observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-id'] });
        document.addEventListener('viewshow', function () {
            close();
            placeAll();
        });
        document.addEventListener('mediareports:seen', function () {
            placeMenuCount(true);
        });
        document.addEventListener('mediareports:report', function (event) {
            try {
                openDialog(event.detail.itemId, !!event.detail.fromPlayer);
            } catch (error) {
                warn('could not open the report dialog', error);
            }
        });
        placeAll();
    }

    window.MediaReports = { placeAll: placeAll, report: report, close: close, myReports: openMine };

    if (typeof window.MutationObserver === 'function') {
        if (document.body) {
            start();
        } else {
            document.addEventListener('DOMContentLoaded', start);
        }
    }
})();
