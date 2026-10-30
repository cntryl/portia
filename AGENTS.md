# Validation authority

Apply the user's validation model in design, implementation, review, tests, and examples. The
full architectural explanation is in [validation authority](docs/validation-authority.md).

- An aggregate guarantees only invariants it can enforce within its atomic consistency boundary.
- Cross-aggregate reads are allowed as advisory validation. They may reject predictable failures
  proactively; a successful read is not a cross-aggregate consistency guarantee.
- When an operation relies on cross-aggregate business assumptions, choreography must subsequently
  obtain authoritative outcomes from the participants, such as a committed inventory reservation
  or reservation rejection.
- A successful advisory check must never eliminate authoritative outcome validation.
- Failed outcome validation must have an explicit business resolution: reject, compensate, or flag
  for intervention. Preserve pending and unknown states until the outcome is resolved.
- Exercise races where an advisory check succeeds and the participant later refuses the operation.
  Verify explicit resolution, replay/idempotency, and that completion requires the authoritative outcome.

An expected business rejection is a domain outcome to record. A failed projector/reactor `Result`
means processing is incomplete and prevents checkpoint advancement; retry or worker failure does
not replace the required business outcome. Projectors build derived data; reactors and commands
perform outcome-validation choreography.
