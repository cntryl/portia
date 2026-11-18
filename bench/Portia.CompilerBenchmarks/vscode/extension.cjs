'use strict';
const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const {performance} = require('node:perf_hooks');
const {matches, replacement, executionTrigger} = require('./state.cjs');

let running = false;

function activate(context) {
    context.subscriptions.push(vscode.commands.registerCommand('portia.compilerBenchmarks.run', () => run()));
}

function watch(document, code, marker, version, timeoutMs, absent = false) {
    let listener;
    let timer;
    let rejectPromise;
    let events = 0;
    const started = performance.now();
    const promise = new Promise((resolve, reject) => {
        rejectPromise = reject;
        listener = vscode.languages.onDidChangeDiagnostics(event => {
            if (!event.uris.some(uri => uri.toString() === document.uri.toString())) return;
            events++;
            if (document.version > version) return reject(new Error('Document changed outside the measured request.'));
            if (document.version !== version) return;
            const diagnostic = vscode.languages.getDiagnostics(document.uri).find(item => matches(item, code, marker));
            if (absent ? !diagnostic : diagnostic) resolve({
                milliseconds: performance.now() - started,
                version,
                code,
                marker
            });
        });
        timer = setTimeout(() => reject(new Error(`Timed out waiting for ${code} with marker ${marker}; ` +
            JSON.stringify({
                expectedVersion: version, actualVersion: document.version, events,
                diagnostics: vscode.languages.getDiagnostics(document.uri).map(item => ({
                    code: item.code,
                    message: item.message
                }))
            }))), timeoutMs);
    });
    // Attach the rejection handler before invoking an edit, which may itself fail.
    promise.catch(() => {
    });
    return {
        promise,
        cancel(error) {
            rejectPromise(error);
        },
        dispose() {
            clearTimeout(timer);
            listener.dispose();
        }
    };
}

async function editAndObserve(document, text, code, marker, timeoutMs) {
    const trigger = executionTrigger(vscode.workspace.getConfiguration('dotnet.server', document.uri).get('sourceGeneratorExecution'));
    const delta = replacement(document.getText(), text);
    const pending = watch(document, code, marker, document.version + 1, timeoutMs);
    try {
        const edit = new vscode.WorkspaceEdit();
        edit.replace(document.uri, new vscode.Range(document.positionAt(delta.start), document.positionAt(delta.end)), delta.text);
        if (!await vscode.workspace.applyEdit(edit)) throw new Error('VS Code rejected the replay edit.');
        if (trigger === 'edit-and-save' && !await document.save()) throw new Error('VS Code could not save the measured edit.');
        return {...await pending.promise, trigger};
    } catch (error) {
        pending.cancel(error);
        throw error;
    } finally {
        pending.dispose();
    }
}

async function run(options = {}) {
    if (running) throw new Error('A compiler replay is already running.');
    running = true;
    const samples = options.samples ?? 3;
    const timeoutMs = options.timeoutMs ?? 60000;
    const folder = vscode.workspace.workspaceFolders;
    if (folder?.length !== 1 || folder[0].uri.scheme !== 'file') {
        running = false;
        throw new Error('Open exactly one disposable local benchmark workspace.');
    }
    const root = folder[0].uri.fsPath;
    const output = path.join(root, `editor-${crypto.randomUUID()}.jsonl`);
    const documents = new Map();
    const originals = new Map();
    let failure;
    let validated = false;
    const append = value => fs.appendFile(output, JSON.stringify(value) + '\n');
    try {
        const manifest = JSON.parse(await fs.readFile(path.join(root, 'portia-benchmark-workspace.json'), 'utf8'));
        if (manifest.format !== 'portia-editor-benchmark-1' ||
            JSON.stringify(manifest.files) !== JSON.stringify(['Projector.cs', 'Request.cs', 'Editing.cs']))
            throw new Error('Unsupported disposable workspace manifest.');
        validated = true;
        for (const file of manifest.files) {
            const content = await fs.readFile(path.join(root, file), 'utf8');
            if (crypto.createHash('sha256').update(content).digest('hex').toUpperCase() !== manifest.initialHashes[file])
                throw new Error(`Fixture has changed before replay: ${file}`);
            originals.set(file, content);
            const document = await vscode.workspace.openTextDocument(vscode.Uri.file(path.join(root, file)));
            if (document.isDirty || document.getText() !== content) throw new Error('Disposable fixture has an existing unsaved edit.');
            documents.set(file, document);
        }
        await append({
            kind: 'environment', vscode: vscode.version, platform: process.platform, arch: process.arch,
            adapterHashes: Object.fromEntries(await Promise.all(['extension.cjs', 'state.cjs'].map(async file =>
                [file, crypto.createHash('sha256').update(await fs.readFile(path.join(__dirname, file))).digest('hex')]))),
            extensions: vscode.extensions.all.filter(extension => extension.packageJSON.publisher !== 'vscode')
                .map(extension => ({
                    id: extension.id,
                    version: extension.packageJSON.version,
                    active: extension.isActive
                })),
            analysis: {
                analyzer: vscode.workspace.getConfiguration('dotnet.backgroundAnalysis').get('analyzerDiagnosticsScope'),
                compiler: vscode.workspace.getConfiguration('dotnet.backgroundAnalysis').get('compilerDiagnosticsScope'),
                sourceGeneratorExecution: vscode.workspace.getConfiguration('dotnet.server').get('sourceGeneratorExecution')
            },
            subjectAssemblies: manifest.subjectAssemblies, samples,
            completion: 'A fresh matching marker acknowledges one diagnostic; the public API does not expose completion of all C# analysis.'
        });
        const projector = documents.get('Projector.cs');
        await vscode.window.showTextDocument(projector);
        let oldProjection = 'Projection_PORTIA_BENCH_0';
        for (let i = 1; i <= samples; i++) {
            const marker = `Projection_PORTIA_BENCH_${i}`;
            const row = await editAndObserve(projector, projector.getText().replaceAll(oldProjection, marker), 'PORTIA002', marker, timeoutMs);
            await append({
                kind: 'sample',
                scenario: 'processor-diagnostic',
                firstRequest: i === 1,
                completeAnalysisMilliseconds: null, ...row
            });
            oldProjection = marker;
        }
        const request = documents.get('Request.cs');
        await vscode.window.showTextDocument(request);
        let oldPattern = '.*';
        for (let i = 1; i <= samples; i++) {
            const marker = `PORTIA_BENCH_METADATA_${i}`;
            const pattern = `(?<${marker}>`;
            const row = await editAndObserve(request, request.getText().replace(JSON.stringify(oldPattern), JSON.stringify(pattern)), 'PORTIA031', marker, timeoutMs);
            await append({kind: 'sample', scenario: 'metadata-diagnostic', completeAnalysisMilliseconds: null, ...row});
            oldPattern = pattern;
        }
        const editing = documents.get('Editing.cs');
        await vscode.window.showTextDocument(editing);
        let oldMarker = 'PORTIA_BENCH_0';
        for (let i = 1; i <= samples; i++) {
            const marker = `PORTIA_BENCH_BODY_${i}`;
            const text = editing.getText().replace(oldMarker, marker).replace(/=> \d+;/, `=> ${i + 1};`);
            const row = await editAndObserve(editing, text, 'CS1030', marker, timeoutMs);
            await append({
                kind: 'sample',
                scenario: 'body-syntax-feedback',
                completeAnalysisMilliseconds: null, ...row
            });
            oldMarker = marker;
        }
        await vscode.window.showTextDocument(projector);
        const diagnostic = vscode.languages.getDiagnostics(projector.uri).find(item => matches(item, 'PORTIA002', oldProjection));
        if (!diagnostic) throw new Error('Processor diagnostic disappeared before code-fix discovery.');
        const started = performance.now();
        const actions = await vscode.commands.executeCommand('vscode.executeCodeActionProvider', projector.uri, diagnostic.range, 'quickfix', 20);
        const milliseconds = performance.now() - started;
        const action = actions?.find(item => item.title === 'Make processor partial');
        if (!action) throw new Error('The expected Portia code fix was not returned by VS Code.');
        await append({
            kind: 'sample',
            scenario: 'code-fix-discovery-and-resolution',
            milliseconds,
            actions: actions.map(item => item.title)
        });
        const cleared = watch(projector, 'PORTIA002', oldProjection, projector.version + 1, timeoutMs, true);
        try {
            const applyStarted = performance.now();
            if (action.edit) {
                if (action.edit.entries().some(([uri]) => ![...documents.values()].some(document => document.uri.toString() === uri.toString())))
                    throw new Error('The code fix attempted to change a file outside the disposable fixture.');
                if (!await vscode.workspace.applyEdit(action.edit)) throw new Error('The host rejected the code-fix edit.');
            } else {
                const command = typeof action.command === 'string' ? action : action.command;
                if (!command?.command) throw new Error('The resolved code fix has no edit or command.');
                await vscode.commands.executeCommand(command.command, ...(command.arguments ?? []));
            }
            const trigger = executionTrigger(vscode.workspace.getConfiguration('dotnet.server', projector.uri).get('sourceGeneratorExecution'));
            if (trigger === 'edit-and-save' && !await projector.save()) throw new Error('Could not save the code-fix edit.');
            const applyMilliseconds = performance.now() - applyStarted;
            if (!projector.getText().includes('partial class ' + oldProjection)) throw new Error('The code fix did not make the processor partial.');
            const feedback = await cleared.promise;
            await append({
                kind: 'sample', scenario: 'code-fix-application', milliseconds: applyMilliseconds,
                diagnosticClearFeedbackMilliseconds: feedback.milliseconds, completeAnalysisMilliseconds: null, trigger
            });
        } catch (error) {
            cleared.cancel(error);
            throw error;
        } finally {
            cleared.dispose();
        }
    } catch (error) {
        failure = error;
        if (validated) await append({kind: 'failed', error: String(error.stack ?? error)});
    } finally {
        try {
            for (const [file, document] of documents) {
                const text = originals.get(file);
                if (document.getText() !== text) {
                    const change = replacement(document.getText(), text);
                    const edit = new vscode.WorkspaceEdit();
                    edit.replace(document.uri, new vscode.Range(document.positionAt(change.start), document.positionAt(change.end)), change.text);
                    if (!await vscode.workspace.applyEdit(edit)) throw new Error('Could not restore disposable fixture: ' + file);
                }
                if (document.isDirty && !await document.save()) throw new Error('Could not save restored fixture: ' + file);
            }
        } catch (error) {
            failure = failure ?? error;
            if (validated) await append({kind: 'failed', phase: 'restore', error: String(error.stack ?? error)});
        } finally {
            running = false;
        }
    }
    if (failure) throw failure;
    await append({
        kind: 'complete',
        restored: true,
        qualification: 'diagnostic host pilot; full analysis completion and controlled-host acceptance remain open'
    });
    return output;
}

module.exports = {activate, run};
