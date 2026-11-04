(() => {
    'use strict';

    const $ = (sel, root = document) => root.querySelector(sel);
    const ICON_SIZE = 256;
    let app = null;

    function ask({ title, body, ok = 'OK', check = null }) {
        return new Promise((resolve) => {
            const modal = $('#ask-modal');
            $('#ask-title').textContent = title;
            $('#ask-body').innerHTML = '';
            const text = document.createElement('p');
            text.className = 'field-note';
            text.textContent = body;
            $('#ask-body').appendChild(text);
            let box = null;
            if (check) {
                const label = document.createElement('label');
                label.className = 'facade-check-row';
                label.innerHTML = '<input type="checkbox" class="facade-switch"><span></span>';
                label.querySelector('span').textContent = check.label;
                box = label.querySelector('input');
                box.checked = !!check.checked;
                $('#ask-body').appendChild(label);
            }
            $('#ask-ok').textContent = ok;
            modal.hidden = false;
            const done = (answer) => {
                modal.hidden = true;
                for (const [el, fn] of handlers) el.removeEventListener('click', fn);
                resolve({ ok: answer, checked: !!(box && box.checked) });
            };
            const handlers = [[$('#ask-ok'), () => done(true)], [$('#ask-cancel'), () => done(false)], [$('#ask-close'), () => done(false)]];
            for (const [el, fn] of handlers) el.addEventListener('click', fn);
            $('#ask-ok').focus();
        });
    }

    function render() {
        if (!app) return;
        const grid = $('#coll-grid');
        const list = app.state.collections || [];
        const packs = (app.state.data && app.state.data.packs) || [];
        $('#collections-count').hidden = !app.state.data;
        $('#collections-count').textContent = list.length;
        $('#coll-empty').hidden = list.length > 0;
        $('#coll-empty').textContent = !app.state.data ? 'Loading…' : 'No collections yet. Make one, or import a .zip.';
        grid.innerHTML = '';
        for (const coll of list) {
            const cards = app.state.list.filter((c) => app.isMember(c, coll)).length;
            const packCount = packs.filter((p) => p.collectionId === coll.id).length;
            const tile = document.createElement('div');
            tile.className = 'addon-tile';
            tile.innerHTML = `
                <div class="addon-thumb-wrap">
                    <button type="button" class="addon-thumb addon-thumb-edit" title="Change icon">${coll.thumbUrl ? `<img alt="" src="${coll.thumbUrl}">` : ''}</button>
                    ${coll.thumbUrl ? '<button type="button" class="facade-iconbtn addon-thumb-clear" title="Remove icon" aria-label="Remove icon">×</button>' : ''}
                    <input type="file" accept="image/*" hidden>
                </div>
                <div class="addon-info">
                    <b class="addon-name"></b>
                    <small class="addon-meta">${cards} card${cards === 1 ? '' : 's'} · ${packCount} booster pack${packCount === 1 ? '' : 's'}</small>
                </div>
                <div class="addon-actions">
                    <button type="button" class="facade-btn fx-sm" data-act="edit">Edit</button>
                    <button type="button" class="facade-btn fx-sm" data-act="export">Export</button>
                    <button type="button" class="facade-btn fx-sm fx-red" data-act="delete">Delete</button>
                </div>`;
            tile.querySelector('.addon-name').textContent = coll.data.name;
            if (!coll.thumbUrl) {
                const canvas = document.createElement('canvas');
                tile.querySelector('.addon-thumb').appendChild(canvas);
                drawThumb(canvas, coll);
            }
            const iconButton = tile.querySelector('.addon-thumb-edit'), iconInput = tile.querySelector('input[type="file"]');
            iconButton.addEventListener('click', () => iconInput.click());
            iconInput.addEventListener('change', () => {
                const file = iconInput.files[0];
                iconInput.value = '';
                if (file) changeIcon(coll, file);
            });
            iconButton.addEventListener('dragover', (e) => {
                if ([...e.dataTransfer.items].some((i) => i.type.startsWith('image/'))) { e.preventDefault(); e.stopPropagation(); }
            });
            iconButton.addEventListener('drop', (e) => {
                const file = [...e.dataTransfer.files].find((f) => f.type.startsWith('image/'));
                if (!file) return;
                e.preventDefault();
                e.stopPropagation();
                changeIcon(coll, file);
            });
            const clear = tile.querySelector('.addon-thumb-clear');
            if (clear) clear.addEventListener('click', () => changeIcon(coll, null));
            tile.querySelector('[data-act="edit"]').addEventListener('click', () => app.openCollection(coll));
            tile.querySelector('[data-act="export"]').addEventListener('click', (e) => exportCollection(coll, e.currentTarget));
            tile.querySelector('[data-act="delete"]').addEventListener('click', (e) => app.confirmMenu(e.currentTarget, `Delete "${coll.data.name}"?`,
                cards ? `Its ${cards} card${cards === 1 ? '' : 's'} and ${packCount} booster pack${packCount === 1 ? '' : 's'} are deleted too. Export it first to keep a copy.` : 'It has no cards.',
                'Delete', () => app.deleteCollection(coll)));
            grid.appendChild(tile);
        }
    }

    function drawThumb(canvas, coll) {
        const first = app.state.list.find((c) => app.isMember(c, coll) && c.thumbUrl);
        if (!first) return;
        const img = new Image();
        img.onload = () => {
            canvas.width = img.naturalWidth;
            canvas.height = img.naturalHeight;
            canvas.getContext('2d').drawImage(img, 0, 0);
        };
        img.src = first.thumbUrl;
    }

    async function iconBlob(file) {
        const bitmap = await createImageBitmap(file);
        const k = Math.min(1, ICON_SIZE / Math.max(bitmap.width, bitmap.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(bitmap.width * k));
        canvas.height = Math.max(1, Math.round(bitmap.height * k));
        const g = canvas.getContext('2d');
        g.imageSmoothingQuality = 'high';
        g.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close();
        return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Could not encode the picture'))), 'image/png'));
    }

    async function changeIcon(coll, file) {
        try {
            if (file) await DaApi.request('PUT', `/api/collections/${coll.id}/thumb`, await iconBlob(file));
            else await DaApi.del(`/api/collections/${coll.id}/thumb`);
            await app.rescan();
            app.toast.ok(file ? 'Icon changed' : 'Icon removed', coll.data.name);
        } catch (e) {
            app.toast.err('Could not change the icon', e.message);
        }
    }

    async function exportCollection(coll, button) {
        button.disabled = true;
        try {
            const job = await DaApi.post(`/api/collections/${coll.id}/export`);
            CCStatus.track(job, { failTitle: 'Could not export the collection' });
        } catch (e) {
            app.toast.err('Could not export the collection', e.message);
        } finally {
            button.disabled = false;
        }
    }

    async function importFile(file) {
        if (!file) return;
        const button = $('#coll-import'), label = button.textContent;
        button.disabled = true;
        button.textContent = 'Uploading…';
        let info;
        try {
            info = await DaApi.request('POST', '/api/import/inspect', file);
        } catch (e) {
            app.toast.err('Could not read the .zip', e.message);
            button.disabled = false;
            button.textContent = label;
            return;
        }
        button.disabled = false;
        button.textContent = label;
        const old = info.kind === 'addon';
        const answer = await ask({
            title: old ? 'Import an old addon' : `Import ${info.name}`,
            body: old
                ? 'This .zip is an addon from DaCard 1.x. Its collections, cards and booster packs are moved into the database.'
                : info.installed
                    ? `"${info.installed}" is already installed. Replace it with this one? Copies players own stay valid.`
                    : `${info.cards} card${info.cards === 1 ? '' : 's'}.`,
            ok: info.installed ? 'Replace' : 'Import',
            check: old ? { label: 'Replace collections that are already installed', checked: false } : null,
        });
        if (!answer.ok) return;
        const replace = info.installed || answer.checked;
        try {
            const job = await DaApi.post(`/api/import?upload=${encodeURIComponent(info.upload)}${replace ? '&replace=1' : ''}${info.name ? `&name=${encodeURIComponent(info.name)}` : ''}`);
            CCStatus.track(job, { failTitle: 'Could not import the collection' });
        } catch (e) {
            app.toast.err('Could not import the collection', e.message);
        }
    }

    async function importFileWithoutDialog(file)
    {
        let info;
        try {
            info = await DaApi.request('POST', '/api/import/inspect', file);
        } catch (e) {
            app.toast.err(`Could not read ${file.name}`, e.message);
            return;
        }

        const replace = info.installed;
        try {
            const job = await DaApi.post(
                `/api/import?upload=${encodeURIComponent(info.upload)}${replace ? '&replace=1' : ''}${info.name ? `&name=${encodeURIComponent(info.name)}` : ''}`
            );
            CCStatus.track(job, {
                failTitle: `Could not import ${info.name || file.name}`
            });
        } catch (e) {
            app.toast.err(`Could not import ${file.name}`, e.message);
        }
    }

    function init(deps) {
        app = deps;
        $('#coll-create').addEventListener('click', () => app.openCollection(null));
        $('#coll-import').addEventListener('click', () => $('#coll-file').click());
        $('#coll-file').addEventListener('change', async (e) => {
            const files = e.target.files;
            e.target.value = '';
            if (files.length === 1)
            {
                await importFile(files[0]);
                return;
            }
            for (const file of files)
            {
                await importFileWithoutDialog(file);
            }
        });
        const pane = $('#pane-collections');
        pane.addEventListener('dragover', (e) => { e.preventDefault(); });
        pane.addEventListener('drop', (e) => {
            e.preventDefault();
            const file = [...(e.dataTransfer.files || [])].find((f) => /\.zip$/i.test(f.name));
            if (file) importFile(file);
        });
    }

    window.CCCollections = { init, render };
})();
