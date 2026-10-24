export async function copyFullPath(path) {
    await navigator.clipboard.writeText(path);
}
