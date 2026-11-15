namespace Cntryl.Portia;

// Implements both unary shapes, which a request reaches by combining a no-result request-family
// interface with IRequest<TOut>. Only one shape can be handled.
sealed record TwoShapeRequest : IRequest, IRequest<int>;
