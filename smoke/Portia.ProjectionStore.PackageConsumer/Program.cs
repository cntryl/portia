using Cntryl.Portia;
using Cntryl.Portia.Testing;

var committed = AggregateOutcome.CommitOnSuccess(Result.Success);
if (committed.Disposition is not AggregateDisposition.Commit)
    throw new InvalidOperationException("A successful result was not committed.");

var rejected = new RequestError(RequestErrorKind.Validation, "rejected");
var discarded = AggregateOutcome.CommitOnSuccess(Result<int>.Failure(rejected));
if (discarded.Disposition is not AggregateDisposition.Discard || !ReferenceEquals(discarded.Result.Error, rejected))
    throw new InvalidOperationException("A failed result was not preserved and discarded.");

await ProjectionStoreConformance.VerifyAsync(new UserlandProjectionStoreProbe());
