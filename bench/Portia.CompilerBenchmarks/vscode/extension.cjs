'use strict';
const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const {performance} = require('node:perf_hooks');
const {matches, replacement, executionTrigger, validateManifest, acceptsFeedback} = require('./state.cjs');

let running = false;

async function verifyDependencies(root, manifest) {
    for (const [file, expected] of [
        ...manifest.scaffoldFiles.map(file => [path.join(root, file), manifest.scaffoldHashes[file]]),
        ...Object.entries(manifest.subjectAssemblies)
    ]) {
        const actual = crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex').toUpperCase();
        if (actual !== expected) throw new Error(`Fixture dependency has changed: ${file}`);
    }
}

function activate(context) {
    context.subscriptions.push(vscode.commands.registerCommand('portia.compilerBenchmarks.run', () => run()));
}

function watch(document, code, marker, version, timeoutMs, absent = false, cleared = [], source) {
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
            if (source && source.document.version > source.version)
                return reject(new Error('Source document changed outside the measured request.'));
            if (source && source.document.version !== source.version) return;
            const diagnostics = vscode.languages.getDiagnostics(document.uri);
            if (acceptsFeedback(diagnostics, code, marker, absent, cleared)) resolve({
                milliseconds: performance.now() - started,
                version,
                ...(source ? {sourceVersion: source.version, consumer: document.uri.toString()} : {}),
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

async function editAndObserve(document, text, code, marker, timeoutMs, cleared = [], consumer) {
    const trigger = executionTrigger(vscode.workspace.getConfiguration('dotnet.server', document.uri).get('sourceGeneratorExecution'));
    const delta = replacement(document.getText(), text);
    const pending = consumer
        ? watch(consumer, code, marker, consumer.version, timeoutMs, false, cleared,
            {document, version: document.version + 1})
        : watch(document, code, marker, document.version + 1, timeoutMs, false, cleared);
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
    let manifest;
    const append = value => fs.appendFile(output, JSON.stringify(value) + '\n');
    try {
        manifest = validateManifest(JSON.parse(await fs.readFile(path.join(root, 'portia-benchmark-workspace.json'), 'utf8')));
        validated = true;
        await verifyDependencies(root, manifest);
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
        await editAndObserve(request, '#warning PORTIA_METADATA_RESTORED\n' +
            request.getText().replace(JSON.stringify(oldPattern), JSON.stringify('.*')),
        'CS1030', 'PORTIA_METADATA_RESTORED', timeoutMs,
        [{code: 'PORTIA031', marker: `PORTIA_BENCH_METADATA_${samples}`}]);
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
        for (let i = 1; i <= samples; i++) {
            const missing = `Missing_PORTIA_INCOMPLETE_${i}`;
            const broken = editing.getText().replace('int Value()', `${missing} Value()`);
            const incomplete = await editAndObserve(editing, broken, 'CS0246', missing, timeoutMs);
            await append({kind: 'sample', scenario: 'incomplete-edit', completeAnalysisMilliseconds: null, ...incomplete});
            const marker = `PORTIA_INCOMPLETE_REPAIR_${i}`;
            const repaired = editing.getText().replace(`${missing} Value()`, 'int Value()').replace(oldMarker, marker);
            const repair = await editAndObserve(editing, repaired, 'CS1030', marker, timeoutMs,
                [{code: 'CS0246', marker: missing}]);
            await append({kind: 'sample', scenario: 'incomplete-edit-repair', completeAnalysisMilliseconds: null, ...repair});
            oldMarker = marker;
        }
        const contract = documents.get('Contracts/SharedContract.cs');
        const consumer = documents.get('CrossProject.cs');
        await vscode.window.showTextDocument(consumer);
        await editAndObserve(consumer, consumer.getText().replace('PORTIA_CROSS_REPAIR_0', 'PORTIA_CROSS_BASELINE'),
            'CS1030', 'PORTIA_CROSS_BASELINE', timeoutMs);
        if (vscode.languages.getDiagnostics(consumer.uri).some(item => matches(item, 'CS1061', 'Value_PORTIA_CROSS_0')))
            throw new Error('Cross-project consumer was invalid before replay.');
        let oldMember = 'Value_PORTIA_CROSS_0';
        let oldRepair = 'PORTIA_CROSS_BASELINE';
        for (let i = 1; i <= samples; i++) {
            const member = `Value_PORTIA_CROSS_${i}`;
            const propagation = await editAndObserve(contract, contract.getText().replace(oldMember, member),
                'CS1061', oldMember, timeoutMs, [], consumer);
            await append({kind: 'sample', scenario: 'cross-project-propagation', completeAnalysisMilliseconds: null, ...propagation});
            const marker = `PORTIA_CROSS_REPAIR_${i}`;
            const repaired = consumer.getText().replace(oldMember, member).replace(oldRepair, marker);
            const repair = await editAndObserve(consumer, repaired, 'CS1030', marker, timeoutMs,
                [{code: 'CS1061', marker: oldMember}]);
            await append({kind: 'sample', scenario: 'cross-project-repair', completeAnalysisMilliseconds: null, ...repair});
            oldMember = member;
            oldRepair = marker;
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
                if (await fs.readFile(path.join(root, file), 'utf8') !== text)
                    throw new Error('Restored fixture content changed during save: ' + file);
            }
            if (validated) await verifyDependencies(root, manifest);
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
