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

    function placeAll() {
        try {
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
        + '.mediaReportsDialog.mr-panel{top:0;right:0;width:400px;max-width:100vw;height:100vh;}'
        + '.mediaReportsHeader{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;}'
        + '.mediaReportsHeading{margin:0;font-size:1.4em;font-weight:500;}'
        + '.mediaReportsTitle{margin:4px 0 0;color:rgba(255,255,255,.7);}'
        + '.mediaReportsClose{background:none;border:0;color:inherit;cursor:pointer;padding:4px;border-radius:50%;}'
        + '.mediaReportsLabel{margin:24px 0 8px;font-weight:500;}'
        + '.mediaReportsReasons{display:flex;flex-wrap:wrap;gap:8px;}'
        + '.mediaReportsReason{height:40px;padding:0 16px;border-radius:20px;border:1px solid rgba(255,255,255,.3);background:transparent;'
        + 'color:inherit;font:inherit;cursor:pointer;}'
        + '.mr-phone .mediaReportsReason{height:44px;border-radius:22px;}'
        + '.mr-remote .mediaReportsReason{height:56px;border-radius:0;padding:0 24px;}'
        + '.mediaReportsReason[aria-pressed=true]{background:#00a4dc;border-color:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsReason:disabled{opacity:.4;cursor:default;}'
        + '.mediaReportsNote{box-sizing:border-box;width:100%;min-height:96px;background:rgba(255,255,255,.08);color:inherit;font:inherit;'
        + 'border:1px solid rgba(255,255,255,.3);border-radius:4px;padding:8px;resize:vertical;}'
        + '.mediaReportsCount{margin-top:4px;font-size:.85em;color:rgba(255,255,255,.6);text-align:right;}'
        + '.mediaReportsPlayback,.mediaReportsHint{margin:16px 0 0;color:rgba(255,255,255,.7);font-size:.9em;}'
        + '.mediaReportsInfo,.mediaReportsError{margin:16px 0 0;padding:12px 16px;border-radius:4px;}'
        + '.mediaReportsInfo{background:rgba(0,164,220,.12);border-left:4px solid #00a4dc;}'
        + '.mediaReportsError{background:rgba(244,67,54,.15);border-left:4px solid #f44336;}'
        + '.mediaReportsActions{margin-top:24px;display:flex;gap:12px;}'
        + '.mediaReportsSend,.mediaReportsDone,.mediaReportsCancel{flex:1;min-height:44px;border:0;border-radius:4px;font:inherit;font-weight:500;cursor:pointer;}'
        + '.mediaReportsSend,.mediaReportsDone{background:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsSend:disabled{background:rgba(255,255,255,.12);color:rgba(255,255,255,.5);cursor:default;}'
        + '.mediaReportsCancel{background:rgba(255,255,255,.12);color:inherit;}'
        // With a remote only the focused button is highlighted, and the buttons are stacked.
        + '.mr-remote .mediaReportsActions{flex-direction:column;}'
        + '.mr-remote .mediaReportsSend,.mr-remote .mediaReportsDone{flex:none;min-height:56px;background:rgba(255,255,255,.12);color:inherit;}'
        + '.mr-remote .mediaReportsCancel{flex:none;min-height:56px;}'
        + '.mr-remote .mediaReportsSend:disabled{background:transparent;color:rgba(255,255,255,.5);}'
        + '.mediaReportsSent{margin-top:24px;}'
        + '.mediaReportsSent strong{display:block;font-size:1.2em;margin-bottom:8px;}'
        + '.mediaReportsDialog.mr-remote *:focus{outline:none;background:#00a4dc;border-color:#00a4dc;color:rgba(0,0,0,.87);}'
        + '.mediaReportsDialog :focus-visible{outline:2px solid #00a4dc;outline-offset:2px;}';

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
        var request = { type: method, url: api.getUrl(path, query || {}), dataType: 'json' };
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
    }

    function openDialog(itemId, fromPlayer) {
        close();
        addStyles();
        // The player keeps the video in view with a side panel, also in the TV layout.
        var tv = isTv();
        var layout = fromPlayer ? 'panel' : tv ? 'tv' : window.innerWidth < 600 ? 'phone' : 'desktop';
        var state = { itemId: itemId, fromPlayer: fromPlayer, layout: layout, tv: tv, reason: null, sending: false, timer: 0, returnTo: document.activeElement };
        open = state;

        state.backdrop = el('div', 'mediaReportsBackdrop' + (layout === 'panel' ? ' mr-clear' : ''));
        state.backdrop.addEventListener('click', close);
        var dialog = el('div', 'mediaReportsDialog mr-' + layout + (tv ? ' mr-remote' : ''));
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.setAttribute('aria-label', LABEL);
        state.dialog = dialog;

        var header = el('div', 'mediaReportsHeader');
        var titles = el('div');
        titles.appendChild(el('h2', 'mediaReportsHeading', LABEL));
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
                close();
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
        document.addEventListener('mediareports:report', function (event) {
            try {
                openDialog(event.detail.itemId, !!event.detail.fromPlayer);
            } catch (error) {
                warn('could not open the report dialog', error);
            }
        });
        placeAll();
    }

    window.MediaReports = { placeAll: placeAll, report: report, close: close };

    if (typeof window.MutationObserver === 'function') {
        if (document.body) {
            start();
        } else {
            document.addEventListener('DOMContentLoaded', start);
        }
    }
})();
