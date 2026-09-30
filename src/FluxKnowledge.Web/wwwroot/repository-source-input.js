window.fluxKnowledge ??= {};
window.fluxKnowledge.sources = (() => {
    // Large pattern text travels directly over HTTP, never as a Blazor circuit message.
    const previews = new WeakMap();
    const status = (form, value) => { form.querySelector('[data-source-status]').textContent = value; };
    const body = form => JSON.stringify({ action: 'root_create', payload: {
        path: form.elements.path.value, displayName: form.elements.displayName.value,
        discoveryMode: 'git-tracked', indexSourceText: true,
        includePatterns: rules(form.elements.includePatterns.value),
        excludePatterns: rules(form.elements.excludePatterns.value)
    } });
    const rules = value => value.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
    const invalidate = form => {
        previews.delete(form); form.querySelector('[data-source-commit]').disabled = true;
        status(form, 'Preview the current settings before saving.');
    };
    async function send(mode, value, key) {
        const headers = { 'Content-Type': 'application/json' };
        if (key) headers['Idempotency-Key'] = key;
        const response = await fetch(`/api/v1/corpus/actions/${mode}`, { method: 'POST', headers, body: value, credentials: 'same-origin' });
        const result = await response.json();
        if (!response.ok || !result.ok) throw new Error(result.reasonCode ?? result.reason_code ?? `Request failed (${response.status}).`);
        return result.result;
    }
    async function preview(form) {
        invalidate(form);
        if (!form.reportValidity()) return;
        const value = body(form);
        status(form, 'Checking repository settings…');
        try {
            const result = await send('preview', value);
            if (body(form) !== value) return;
            previews.set(form, { value, confirmationId: result.confirmationId, key: crypto.randomUUID() });
            form.querySelector('[data-source-commit]').disabled = false;
            const payload = JSON.parse(value).payload;
            status(form, `${result.effectSummary} Git-tracked code text and documentation update automatically. ` +
                `${payload.includePatterns.length} include rules and ${payload.excludePatterns.length} exclude rules; private, build and model exclusions always apply.`);
        } catch (error) { status(form, error.message); }
    }
    async function commit(form) {
        const preview = previews.get(form);
        if (!preview || body(form) !== preview.value) { invalidate(form); return; }
        form.querySelector('[data-source-commit]').disabled = true;
        const value = JSON.parse(preview.value); value.confirmation_id = preview.confirmationId;
        try {
            await send('commit', JSON.stringify(value), preview.key);
            status(form, 'Repository configured. Indexing has been queued.');
            window.location.assign('/sources');
        } catch (error) { status(form, error.message); form.querySelector('[data-source-commit]').disabled = false; }
    }
    return { invalidate, preview, commit };
})();
