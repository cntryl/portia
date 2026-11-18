'use strict';

function diagnosticCode(diagnostic) {
    return String(diagnostic.code && typeof diagnostic.code === 'object' ? diagnostic.code.value : diagnostic.code);
}

function matches(diagnostic, code, marker) {
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(marker)) throw new Error('Expected a diagnostic identifier marker.');
    return diagnosticCode(diagnostic) === code &&
        new RegExp(`(?:^|[^A-Za-z0-9_])${marker}(?:$|[^A-Za-z0-9_])`).test(diagnostic.message);
}

function replacement(before, after) {
    if (before === after) throw new Error('The replay edit would be a no-op.');
    let start = 0;
    while (start < before.length && start < after.length && before[start] === after[start]) start++;
    let suffix = 0;
    while (suffix < before.length - start && suffix < after.length - start &&
    before[before.length - suffix - 1] === after[after.length - suffix - 1]) suffix++;
    return {start, end: before.length - suffix, text: after.slice(start, after.length - suffix)};
}

function executionTrigger(mode) {
    if (mode === 'Balanced') return 'edit-and-save';
    if (mode === 'Automatic') return 'typing';
    throw new Error('Unsupported source generator execution mode: ' + mode);
}

module.exports = {diagnosticCode, matches, replacement, executionTrigger};
