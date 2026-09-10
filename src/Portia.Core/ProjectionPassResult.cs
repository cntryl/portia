namespace Cntryl.Portia;

readonly record struct ProjectionPassResult(ProjectionCheckpoint Checkpoint, bool BudgetExhausted);
