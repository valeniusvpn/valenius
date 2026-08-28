(function () {
    'use strict';

    var storageKey = 'valenius-admin-density';
    var textSizeStorageKey = 'valenius-admin-text-size';
    var root = document.documentElement;

    function applyDensity(value) {
        var density = value === 'compact' ? 'compact' : 'comfortable';
        root.setAttribute('data-density', density);
        document.querySelectorAll('[data-density-value]').forEach(function (button) {
            var active = button.getAttribute('data-density-value') === density;
            button.classList.toggle('active', active);
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
        });
    }

    document.querySelectorAll('[data-density-value]').forEach(function (button) {
        button.addEventListener('click', function () {
            var density = button.getAttribute('data-density-value');
            try { localStorage.setItem(storageKey, density); } catch (_) { }
            applyDensity(density);
        });
    });

    var preferred = 'comfortable';
    try { preferred = localStorage.getItem(storageKey) || preferred; } catch (_) { }
    applyDensity(preferred);

    var textSizes = ['default', 'large', 'x-large'];
    var textSizeLabels = { 'default': 'Default', 'large': 'Large', 'x-large': 'Extra large' };
    var decreaseText = document.getElementById('textSizeDecrease');
    var increaseText = document.getElementById('textSizeIncrease');
    var textSizeStatus = document.getElementById('textSizeStatus');

    function applyTextSize(value, announce) {
        var index = textSizes.indexOf(value);
        if (index < 0) index = 0;
        var size = textSizes[index];
        root.setAttribute('data-text-size', size);
        if (decreaseText) decreaseText.disabled = index === 0;
        if (increaseText) increaseText.disabled = index === textSizes.length - 1;
        if (textSizeStatus && announce) textSizeStatus.textContent = 'Text size: ' + textSizeLabels[size];
    }

    function changeTextSize(direction) {
        var current = textSizes.indexOf(root.getAttribute('data-text-size'));
        if (current < 0) current = 0;
        var next = Math.max(0, Math.min(textSizes.length - 1, current + direction));
        var value = textSizes[next];
        try { localStorage.setItem(textSizeStorageKey, value); } catch (_) { }
        applyTextSize(value, true);
    }

    var preferredTextSize = 'default';
    try { preferredTextSize = localStorage.getItem(textSizeStorageKey) || preferredTextSize; } catch (_) { }
    applyTextSize(preferredTextSize, false);
    if (decreaseText) decreaseText.addEventListener('click', function () { changeTextSize(-1); });
    if (increaseText) increaseText.addEventListener('click', function () { changeTextSize(1); });

    /* Preserve real table semantics on desktop and turn operational tables into
       labelled record cards at the mobile breakpoint. Opt out with .mobile-table. */
    document.querySelectorAll('.main-content table.table:not(.mobile-table):not(.firewall-ports)').forEach(function (table) {
        var headings = Array.prototype.map.call(table.querySelectorAll('thead th'), function (th) {
            return th.textContent.replace(/\s+/g, ' ').trim();
        });
        if (!headings.length) return;
        table.classList.add('mobile-cards');
        table.querySelectorAll('tbody tr').forEach(function (row) {
            Array.prototype.forEach.call(row.children, function (cell, index) {
                if (!cell.hasAttribute('data-label')) {
                    cell.setAttribute('data-label', headings[index] || '');
                }
            });
        });
    });

    /* Close the mobile navigation with Escape and keep trigger state available
       to assistive technology. */
    var toggle = document.getElementById('sidebarToggle');
    var sidebar = document.querySelector('.sidebar');
    var backdrop = document.getElementById('sidebarBackdrop');
    function syncSidebarState() {
        if (!toggle || !sidebar) return;
        var open = sidebar.classList.contains('open');
        var mobile = window.matchMedia('(max-width: 767.98px)').matches;
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (mobile && !open) {
            sidebar.setAttribute('inert', '');
            sidebar.setAttribute('aria-hidden', 'true');
        } else {
            sidebar.removeAttribute('inert');
            sidebar.removeAttribute('aria-hidden');
        }
    }
    if (toggle && sidebar) {
        toggle.setAttribute('aria-controls', 'adminSidebar');
        syncSidebarState();
        toggle.addEventListener('click', function () { window.setTimeout(syncSidebarState, 0); });
        if (backdrop) backdrop.addEventListener('click', syncSidebarState);
        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' && sidebar.classList.contains('open')) {
                sidebar.classList.remove('open');
                if (backdrop) backdrop.classList.remove('active');
                syncSidebarState();
                toggle.focus();
            }
        });
        window.addEventListener('resize', syncSidebarState);
    }
}());
