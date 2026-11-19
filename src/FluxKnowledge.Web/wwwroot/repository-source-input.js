window.fluxKnowledge ??= {};
window.fluxKnowledge.sources = (() => {
    // Large pattern text travels directly over HTTP, never as a Blazor circuit message.
    const previews = new WeakMap();
    const ruleEdits = new WeakMap();
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
        if (!response.ok || !result.ok) {
            const reason = result.reasonCode ?? result.reason_code;
            throw Object.assign(new Error(reason ?? `Request failed (${response.status}).`), {
                reason, uncertain: result.retryable || response.status >= 500 || reason === 'operation-failed'
            });
        }
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
    async function saveRules(form) {
        const fields = form.elements;
        const payload = {
            rootId: fields.rootId.value,
            expectedConfigurationRevision: Number(fields.expectedConfigurationRevision.value)
        };
        for (const name of ['includePatterns', 'excludePatterns']) {
            const field = fields[name];
            if (field.value !== field.defaultValue.replace(/\r\n/g, '\n')) {
                payload[name] = field.value.split(/\r?\n/).filter(line => line.trim());
            }
        }
        if (!payload.includePatterns && !payload.excludePatterns) return 'unchanged';

        const command = { action: 'root_update', payload };
        const value = JSON.stringify(command);
        try {
            let attempt = ruleEdits.get(form);
            if (attempt?.value !== value) {
                const preview = await send('preview', value);
                attempt = { value, confirmationId: preview.confirmationId, key: crypto.randomUUID() };
                ruleEdits.set(form, attempt);
            }
            await send('commit', JSON.stringify({ ...command, confirmation_id: attempt.confirmationId }), attempt.key);
            ruleEdits.delete(form);
            return 'saved';
        } catch (error) {
            if (error.reason && !error.uncertain) ruleEdits.delete(form);
            return error.reason ?? 'unconfirmed';
        }
    }
    return { invalidate, preview, commit, saveRules };
})();
