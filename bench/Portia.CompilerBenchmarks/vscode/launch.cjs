'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {spawn} = require('node:child_process');
const crypto = require('node:crypto');
const {executionTrigger} = require('./state.cjs');

const workspace = path.resolve(process.argv[2] ?? 'artifacts/compiler/vscode-pilot');
const generatorExecution = process.argv[3] ?? 'Automatic';
executionTrigger(generatorExecution);
const manifest = JSON.parse(fs.readFileSync(path.join(workspace, 'portia-benchmark-workspace.json'), 'utf8'));
if (manifest.format !== 'portia-editor-benchmark-1') throw new Error('Expected an exported editor benchmark workspace.');
// macOS Unix sockets require a short user-data path, even when artifacts live in a deep checkout.
const profile = fs.mkdtempSync(path.join(process.platform === 'darwin' ? '/tmp' : os.tmpdir(), 'portia-editor-'));
const artifacts = path.join(path.dirname(workspace), 'editor-run-' + crypto.randomUUID());
fs.mkdirSync(artifacts);
fs.writeFileSync(path.join(artifacts, 'profile.json'), JSON.stringify({profile, workspace}));
fs.mkdirSync(path.join(profile, 'User'), {recursive: true});
fs.writeFileSync(path.join(profile, 'User', 'settings.json'), JSON.stringify({
    'extensions.autoUpdate': false,
    'extensions.autoCheckUpdates': false,
    'update.mode': 'none',
    'dotnet.server.sourceGeneratorExecution': generatorExecution
}));
const extensions = path.join(os.homedir(), '.vscode', 'extensions');
const allowed = new Set(['ms-dotnettools.csharp', 'ms-dotnettools.csdevkit', 'ms-dotnettools.vscode-dotnet-runtime']);
const disabled = JSON.parse(fs.readFileSync(path.join(extensions, 'extensions.json'), 'utf8'))
    .map(extension => extension.identifier.id).filter(id => !allowed.has(id.toLowerCase()));
disabled.push('github.copilot', 'github.copilot-chat', 'TypeScriptTeam.jsts-chat-features');
const executable = process.env.VSCODE_EXECUTABLE ?? '/Applications/Visual Studio Code.app/Contents/MacOS/Code';
const args = [
    '--user-data-dir=' + profile, '--extensions-dir=' + extensions,
    '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes',
    '--extensionDevelopmentPath=' + __dirname,
    '--extensionTestsPath=' + path.join(__dirname, 'host-test.cjs'),
    ...disabled.flatMap(id => ['--disable-extension', id]), workspace
];
const env = {...process.env};
delete env.ELECTRON_RUN_AS_NODE;
delete env.VSCODE_IPC_HOOK_CLI;
const log = path.join(artifacts, 'launcher.log');
const fd = fs.openSync(log, 'wx');
const child = spawn(executable, args, {env, stdio: ['ignore', fd, fd]});
console.log('Editor pilot log: ' + log);
let killed = false;
let force;
const timer = setTimeout(() => {
    killed = true;
    child.kill('SIGTERM');
    force = setTimeout(() => child.kill('SIGKILL'), 10000);
}, 180000);
child.on('error', error => {
    clearTimeout(timer);
    console.error(error);
    process.exitCode = 1;
});
child.on('exit', (code, signal) => {
    clearTimeout(timer);
    clearTimeout(force);
    fs.closeSync(fd);
    console.log(`Editor pilot exit: ${code}, signal: ${signal}, timedOut: ${killed}`);
    process.exitCode = code ?? 1;
});
