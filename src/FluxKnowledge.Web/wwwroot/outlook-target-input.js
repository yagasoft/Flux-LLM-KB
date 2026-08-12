window.fluxKnowledge ??= {};
window.fluxKnowledge.outlook ??= {};
window.fluxKnowledge.outlook.readAndClearTargetPath = (input) => {
    const value = input?.value ?? "";
    if (input) input.value = "";
    return value;
};
