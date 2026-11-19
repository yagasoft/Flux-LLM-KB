const prefix = 'fluxknowledge.retained-recovery.';

export function readPending(revision) {
    const value = sessionStorage.getItem(prefix + revision);
    return value ? JSON.parse(value) : null;
}

export function rememberPending(revision, submission) {
    const existing = readPending(revision);
    if (existing) return existing.idempotencyKey === submission.idempotencyKey;
    const count = Object.keys(sessionStorage).filter(key => key.startsWith(prefix)).length;
    if (count >= 16) return false;
    sessionStorage.setItem(prefix + revision, JSON.stringify(submission));
    return true;
}

export function forgetPending(revision, key) {
    if (readPending(revision)?.idempotencyKey === key) sessionStorage.removeItem(prefix + revision);
}

export async function request(form, step, target, confirmationId, idempotencyKey, protectedPolicyPayload) {
    const body = new FormData(form);
    body.set('kind', target.kind);
    body.set('sourceRevisionId', target.sourceRevisionId);
    if (target.exactId) body.set('exactId', target.exactId);
    if (confirmationId) body.set('confirmationId', confirmationId);
    if (idempotencyKey) body.set('idempotencyKey', idempotencyKey);
    if (protectedPolicyPayload) body.set('policyBinding', protectedPolicyPayload);
    const response = await fetch(`/api/operator-actions/retained-recovery/${step}`, {
        method: 'POST', credentials: 'same-origin', body
    });
    if (!response.headers.get('content-type')?.includes('application/json')) return { reasonCode: 'recovery-service-unavailable' };
    return await response.json();
}
