'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {matches, replacement, executionTrigger, acceptsFeedback} = require('./state.cjs');

function capture(action) {
    try {
        action();
    } catch (error) {
        return error;
    }
    return undefined;
}

test('ShouldRequireSaveGivenBalancedGeneratorExecution', () => {
    // Arrange
    const mode = 'Balanced';
    // Act
    const trigger = executionTrigger(mode);
    // Assert
    assert.equal(trigger, 'edit-and-save');
});

test('ShouldMeasureTypingGivenAutomaticGeneratorExecution', () => {
    // Arrange
    const mode = 'Automatic';
    // Act
    const trigger = executionTrigger(mode);
    // Assert
    assert.equal(trigger, 'typing');
});

test('ShouldRejectUnknownTriggerGivenUnsupportedGeneratorExecution', () => {
    // Arrange
    const mode = 'unknown';
    // Act
    const error = capture(() => executionTrigger(mode));
    // Assert
    assert.ok(error instanceof Error);
    assert.match(error.message, /Unsupported/);
});

test('ShouldRejectStaleDiagnosticGivenPreviousRequestMarker', () => {
    // Arrange
    const diagnostic = {code: {value: 'PORTIA031'}, message: 'Invalid pattern PORTIA_BENCH_1'};
    // Act
    const accepted = matches(diagnostic, 'PORTIA031', 'PORTIA_BENCH_2');
    // Assert
    assert.equal(accepted, false);
});

test('ShouldAcceptCurrentSemanticDiagnosticGivenCurrentRequestMarker', () => {
    // Arrange
    const diagnostic = {code: {value: 'PORTIA031'}, message: 'Invalid pattern PORTIA_BENCH_2'};
    // Act
    const accepted = matches(diagnostic, 'PORTIA031', 'PORTIA_BENCH_2');
    // Assert
    assert.equal(accepted, true);
});

test('ShouldRejectLaterRequestGivenMarkerWithSharedPrefix', () => {
    // Arrange
    const diagnostic = {code: 'PORTIA031', message: 'Invalid pattern PORTIA_BENCH_10'};
    // Act
    const accepted = matches(diagnostic, 'PORTIA031', 'PORTIA_BENCH_1');
    // Assert
    assert.equal(accepted, false);
});

test('ShouldPreserveUnchangedTextGivenMinimalReplacement', () => {
    // Arrange
    const before = 'public int Value() => 1;';
    // Act
    const edit = replacement(before, 'public int Value() => 2;');
    // Assert
    assert.deepEqual(edit, {start: 22, end: 23, text: '2'});
});

test('ShouldRejectUnchangedInputGivenRepeatedReplayEdit', () => {
    // Arrange
    const before = 'unchanged';
    // Act
    const error = capture(() => replacement(before, before));
    // Assert
    assert.ok(error instanceof Error);
    assert.match(error.message, /no-op/);
});

test('ShouldRejectOldFixtureGivenChangedReplayContract', () => {
    // Arrange
    const manifest = {format: 'portia-editor-benchmark-1', files: ['Projector.cs', 'Request.cs', 'Editing.cs']};
    // Act
    const error = capture(() => require('./state.cjs').validateManifest(manifest));
    // Assert
    assert.ok(error instanceof Error);
});

test('ShouldRejectMissingProjectGivenCrossProjectReplay', () => {
    // Arrange
    const state = require('./state.cjs');
    const manifest = {format: 'portia-editor-benchmark-2', files: [...state.fixtureFiles], scaffoldFiles: ['Bench.Editor.csproj']};
    // Act
    const error = capture(() => state.validateManifest(manifest));
    // Assert
    assert.ok(error instanceof Error);
});

test('ShouldRejectRepairFeedbackGivenStaleSemanticFailure', () => {
    // Arrange
    const diagnostics = [{code: 'CS1030', message: 'PORTIA_REPAIR_1'},
        {code: 'CS1061', message: 'No member Value_PORTIA_CROSS_0'}];
    // Act
    const accepted = acceptsFeedback(diagnostics, 'CS1030', 'PORTIA_REPAIR_1', false,
        [{code: 'CS1061', marker: 'Value_PORTIA_CROSS_0'}]);
    // Assert
    assert.equal(accepted, false);
});

test('ShouldAcceptRepairFeedbackGivenFreshMarkerAndClearedFailure', () => {
    // Arrange
    const diagnostics = [{code: 'CS1030', message: 'PORTIA_REPAIR_1'}];
    // Act
    const accepted = acceptsFeedback(diagnostics, 'CS1030', 'PORTIA_REPAIR_1', false,
        [{code: 'CS0246', marker: 'Missing_PORTIA_INCOMPLETE_1'}]);
    // Assert
    assert.equal(accepted, true);
});
