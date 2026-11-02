/* Media Reports: adds a "Report a problem" button to jellyfin-web, after the favourite
 * button on movie, episode, season and series pages and in the video player.
 * Served by the plugin and added to jellyfin-web's index page as it is served.
 * Written for the older browsers of TV apps: no modules, arrow functions or classes. */
(function () {
    'use strict';

    var LABEL = 'Report a problem';
    var BUTTON_CLASS = 'mediaReportsButton';
    var REPORTABLE = { Movie: true, Episode: true, Season: true, Series: true };

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
        var button = document.createElement('button');
        button.type = 'button';
        button.title = LABEL;
        button.setAttribute('aria-label', LABEL);
        button.setAttribute('data-id', itemId);
        var icon = document.createElement('span');
        icon.setAttribute('aria-hidden', 'true');
        if (inPlayer) {
            button.className = 'autoSize paper-icon-button-light ' + BUTTON_CLASS;
            icon.className = 'xlargePaperIconButton material-icons flag';
            button.appendChild(icon);
        } else {
            button.className = 'button-flat detailButton emby-button ' + BUTTON_CLASS + (isTv() ? ' show-focus' : '');
            icon.className = 'material-icons detailButton-icon flag';
            var content = document.createElement('div');
            content.className = 'detailButton-content';
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
        document.addEventListener('viewshow', placeAll);
        placeAll();
    }

    window.MediaReports = { placeAll: placeAll, report: report };

    if (typeof window.MutationObserver === 'function') {
        if (document.body) {
            start();
        } else {
            document.addEventListener('DOMContentLoaded', start);
        }
    }
})();
